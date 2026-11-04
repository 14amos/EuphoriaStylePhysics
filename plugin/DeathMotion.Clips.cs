using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EuphoriaStylePhysics;

// user 2026-11-02 ("so the writhing could adapt to however the ragdoll fell, thats what i mean by active ragdoll"): a motion
// clip (ClipLibrary) as targets, not as animation. Each body's muscle swings its target so its bone points where the clip's
// bone points (the twist stays the muscle's own); the support hand follows the clip's hips height and speed, the trunk its tilt;
// a foot plants when the clip has it on the floor. The clip is laid onto the body where and however it is when it starts (turned
// to the body's heading, its hips over the body's pelvis), so a floor clip starts from the way the body really landed.
internal sealed partial class DeathMotion
{
    private MotionClip _clip;
    private bool _clipMirror, _floorTried;
    private float _clipFrom, _clipGround;
    private Quaternion _clipYaw = Quaternion.identity;
    private Vector3 _clipOc, _clipOb, _clipMirN, _clipMirO;
    private Vector3[] _segLocal;          // per body: its bone (from its joint to the next one) in its own frame
    private string[][] _segClip;          // per body: the clip joints of the same bone (from, to), left-side names
    private int[] _segA, _segB;           // the same, as joint indices of the clip playing (mirror applied); -1: none
    private Vector3 _pelvisSegLocal, _chestSegLocal;   // the trunk's bones (pelvis to spine, chest to head), in their own frames
    // joint indices of the clip playing, mirror applied (the hot path looks nothing up by name)
    private int _jHips, _jSpine1, _jChest, _jNeck2, _jLFoot, _jRFoot;
    // 0.20.0: the clip's and the body's trunk frames (hips: legs; chest: arms and head), so limbs follow the clip relative to the trunk
    private int _jLLeg, _jRLeg, _jLArm, _jRArm;
    private Quaternion _relPelvis = Quaternion.identity, _relChest = Quaternion.identity;
    private float _relAt = -1f;
    private int _clipLogs;
    // 2026-11-03 (log: a running death 51 deg off in its first 0.25 s, right upper arm 112): every clip's frame 0 is EFT's standing
    // rifle pose, and a body that dies running (or lands any way at all) is not in it, while the clip took over within 0.12 s. Each
    // bone now starts from where it really points and comes onto the clip's over OffFade (_segOff: clip bone to body bone at start).
    private Quaternion[] _segOff;
    private float OffFade => _clip.Floor ? 1.0f : 0.4f;
    // lying: the clip turned about its head line (hips to head) onto the side the body lies on, faded out over RollFade, so a body
    // on its side rolls over into the clip instead of every limb being told the clip's on-the-back directions from the start
    private float _clipRoll;
    private Vector3 _clipRollAxis, _clipRollO;
    private const float RollFade = 1.5f;
    private EBodyPart _hitPart;           // for the floor clip (a leg wound sits up holding the leg)

    // dev panel (dev build only): the clip the next floor clip uses, whatever the dice say; used once
    internal static string ForceFloorClip;

    private bool ClipOn => _clip != null && _t >= _clipFrom && _clipClock <= _clip.Length;
    private float ClipT => _clipClock;

    // user 2026-11-03 ("the steps dont really look in sync with the ragdolls"; log: hips 0.5-0.8 m behind the clip, limbs ~34 deg
    // off): the clip ran on its own clock, so a body that fell behind was told to take the next step before it got there. The
    // clip's clock now slows (to half) while the body's hips trail the clip's along its path; everything timed after it waits.
    private float _clipClock, _clipLag;

    private void ClipAdvance(float dt)
    {
        if (_clip == null || _t < _clipFrom) return;
        float rate = 1f;
        if (_clipClock < _clip.Length)
        {
            Vector3 want = ClipPos(_jHips, _clipClock);
            Vector3 ahead = Flat3(ClipPos(_jHips, Mathf.Min(_clipClock + 0.2f, _clip.Length)) - want);
            Vector3 gap = Flat3(want - _bodies[_pelvis].Rb.position);
            float behind = ahead.sqrMagnitude > 1e-6f ? Vector3.Dot(gap, ahead.normalized) : 0f;
            rate = Mathf.Lerp(1f, 0.5f, Smooth(0.1f, 0.5f, behind));
        }
        _clipClock += dt * rate;
        float lag = dt * (1f - rate);
        if (lag <= 0f) return;
        _clipLag += lag;
        if (!_clip.Floor) { _dur += lag; ShiftAfterFeet(lag); }   // the death clip's end, lying hurt and the rest wait for it
        else { _hurtTo += lag; if (_throesTo > 0f) { _throesFrom += lag; _throesTo += lag; } }
    }

    // how much the clip rules: eased in over its first moment (from the pose the body has). Past its end it keeps ruling with its
    // last frame (At holds it): user 2026-11-02 ("they like get stuck halfway through the fall sometimes and then do a little jump
    // when their ragdoll settles"): letting go at the end snapped every muscle back to its old target (the standing pose after a
    // death clip, the landed pose after a floor clip) at full strength. The strength fades instead (ClipStrength, the Clip style).
    private float ClipW()
    {
        if (_clip == null || _t < _clipFrom) return 0f;
        return Smooth(0f, _clip.Floor ? 0.8f : 0.12f, ClipT);
    }

    // a floor clip's own muscle: full through the clip, gone over its last 1.2 s (it ends lying still in the clip's last pose)
    private float ClipStrength()
    {
        if (_clip == null || _t < _clipFrom) return 0f;
        return 1f - Smooth(_clip.Length - 1.2f, _clip.Length, ClipT);
    }

    // the bones as the clip names them; set once at death from the pose the bodies have
    private void ClipBones()
    {
        _segLocal = new Vector3[_bodies.Count];
        _segClip = new string[_bodies.Count][];
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            Transform end = null;
            string[] seg = null;
            switch (b.Role)
            {
                case Role.ThighL: case Role.ThighR:
                    seg = new[] { "LeftLeg", "LeftShin" }; end = _bodies[b.Role == Role.ThighL ? _calfL : _calfR].Rb.transform; break;
                case Role.CalfL: case Role.CalfR:
                    seg = new[] { "LeftShin", "LeftFoot" }; end = b.Role == Role.CalfL ? _ankleL : _ankleR; break;
                case Role.UpperL: case Role.UpperR:
                {
                    var a = _arms[b.Role == Role.UpperL ? 0 : 1];
                    if (a.Fore >= 0) { seg = new[] { "LeftArm", "LeftForeArm" }; end = _bodies[a.Fore].Rb.transform; }
                    break;
                }
                case Role.ForeL: case Role.ForeR:
                {
                    var a = _arms[b.Role == Role.ForeL ? 0 : 1];
                    if (a.Palm != null) { seg = new[] { "LeftForeArm", "LeftHand" }; end = a.Palm; }
                    break;
                }
                case Role.Spine:
                    if (i == _chest) { seg = new[] { "Chest", "Neck2" }; end = _head >= 0 ? _bodies[_head].Rb.transform : null; }
                    else
                        foreach (int c in b.Children)
                            if (_bodies[c].Role == Role.Spine) { seg = new[] { "Spine1", "Chest" }; end = _bodies[c].Rb.transform; break; }
                    break;
                case Role.Head:
                    // no joint past the head: its own up (from the neck through it) stands for the bone
                    if (b.Parent >= 0)
                    {
                        _segLocal[i] = Quaternion.Inverse(b.Rb.rotation) * (b.Rb.position - _bodies[b.Parent].Rb.position).normalized;
                        _segClip[i] = new[] { "Neck1", "Head" };
                    }
                    continue;
            }
            if (seg == null || end == null) continue;
            Vector3 d = end.position - b.Rb.position;
            if (d.sqrMagnitude < 1e-4f) continue;
            _segLocal[i] = Quaternion.Inverse(b.Rb.rotation) * d.normalized;
            bool right = b.Role is Role.ThighR or Role.CalfR or Role.UpperR or Role.ForeR;
            _segClip[i] = right ? new[] { Side(seg[0], true), Side(seg[1], true) } : seg;
        }
        // 2026-11-02 review: the trunk was steered by its "up at death" against the clip's spine; EFT's rifle stance leans ~15 deg,
        // so it was always pulled that far off. Both sides now use the same bones: pelvis to the first spine body, chest to head.
        var pelvis = _bodies[_pelvis];
        _pelvisSegLocal = _pelvisUpLocal;
        foreach (int c in pelvis.Children)
            if (_bodies[c].Role == Role.Spine)
            {
                Vector3 d = _bodies[c].Rb.position - pelvis.Rb.position;
                if (d.sqrMagnitude > 1e-4f) _pelvisSegLocal = Quaternion.Inverse(pelvis.Rb.rotation) * d.normalized;
                break;
            }
        _chestSegLocal = _segLocal[_chest] != Vector3.zero ? _segLocal[_chest] : _chestUpLocal;
    }

    private static string Side(string joint, bool flip) =>
        !flip ? joint : joint.StartsWith("Left", StringComparison.Ordinal) ? "Right" + joint.Substring(4)
                      : joint.StartsWith("Right", StringComparison.Ordinal) ? "Left" + joint.Substring(5) : joint;

    // a joint of the clip at clip time t, in the world. j is an index from StartClip, which already swapped left and right for
    // a mirrored clip; the positions are reflected here (played the other way round).
    private Vector3 ClipPos(int j, float t)
    {
        Vector3 p = _clip.At(j, t);
        if (_clipMirror) p -= 2f * Vector3.Dot(p - _clipMirO, _clipMirN) * _clipMirN;
        if (_clipRoll != 0f)
        {
            float r = _clipRoll * (1f - Smooth(0f, RollFade, t));
            if (r > 0.05f || r < -0.05f) p = _clipRollO + Quaternion.AngleAxis(r, _clipRollAxis) * (p - _clipRollO);
        }
        Vector3 w = _clipYaw * (Flat3(p) - _clipOc) + _clipOb;
        w.y = p.y + _clipGround;
        return w;
    }

    private Vector3 ClipDir(int a, int b, float t)
    {
        Vector3 d = ClipPos(b, t) - ClipPos(a, t);
        return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
    }

    private int J(string joint) => _clip.Index(Side(joint, _clipMirror));

    // Lays the clip onto the body as it is now
    private void StartClip(MotionClip clip, bool mirror)
    {
        _clip = clip;
        _clipMirror = mirror;
        _clipFrom = _t;
        _clipClock = 0f;
        _clipLag = 0f;
        _clipLogs = 0;
        _clipRoll = 0f;
        TrackReport();   // start this clip's tracking from nothing
        _jHips = J("Hips"); _jSpine1 = J("Spine1"); _jChest = J("Chest"); _jNeck2 = J("Neck2");
        _jLFoot = J("LeftFoot"); _jRFoot = J("RightFoot");
        _jLLeg = J("LeftLeg"); _jRLeg = J("RightLeg"); _jLArm = J("LeftArm"); _jRArm = J("RightArm");
        _relAt = -1f;
        _segA ??= new int[_bodies.Count];
        _segB ??= new int[_bodies.Count];
        for (int i = 0; i < _bodies.Count; i++)
        {
            var sc = _segClip[i];
            _segA[i] = sc != null ? J(sc[0]) : -1;
            _segB[i] = sc != null ? J(sc[1]) : -1;
            if (_segA[i] < 0 || _segB[i] < 0) _segA[i] = _segB[i] = -1;
        }
        var pelvis = _bodies[_pelvis].Rb;
        float h = HeightAboveGround(pelvis.position);
        _clipGround = h < 5f ? pelvis.position.y - h : _groundY;
        // the clip's own frame at its start: its sagittal plane (for mirroring) through the hips, across its left
        Vector3 hips0 = clip.At(clip.Index("Hips"), 0f);
        _clipMirO = hips0;
        // across its hips in 3D (2026-11-02 review: flattened, a clip lying on its side had almost no left-right axis left)
        Vector3 across = clip.At(clip.Index("LeftLeg"), 0f) - clip.At(clip.Index("RightLeg"), 0f);
        _clipMirN = across.sqrMagnitude > 1e-6f ? across.normalized : Vector3.right;
        // heading: standing, the hips' left; lying, where the head is. The same rule for the clip and the body.
        Vector3 cHips = Raw("Hips"), cChest = Raw("Chest");
        bool upright = (cChest - cHips).normalized.y > 0.6f;
        Vector3 hc = upright ? Flat3(Raw("LeftLeg") - Raw("RightLeg")) : Flat3(Raw("Head") - cHips);
        Vector3 hb = upright ? Flat3(_bodies[_thighL].Rb.position - _bodies[_thighR].Rb.position)
                             : Flat3((_head >= 0 ? _bodies[_head].Rb.position : _bodies[_chest].Rb.position) - pelvis.position);
        _clipYaw = hc.sqrMagnitude > 1e-6f && hb.sqrMagnitude > 1e-6f ? Quaternion.FromToRotation(hc.normalized, hb.normalized) : Quaternion.identity;
        _clipOc = Flat3(cHips);
        _clipOb = Flat3(pelvis.position);
        if (!upright && hc.sqrMagnitude > 1e-6f)
        {
            // the clip's chest front and the body's, both seen along the head line, in the clip's frame (before the yaw)
            Vector3 axis = hc.normalized;
            Vector3 cf = Vector3.ProjectOnPlane(Vector3.Cross(Raw("Neck2") - cHips, Raw("LeftArm") - Raw("RightArm")), axis);
            Vector3 bf = Vector3.ProjectOnPlane(Quaternion.Inverse(_clipYaw) * (_bodies[_chest].Rb.rotation * _chestFwdLocal), axis);
            if (cf.sqrMagnitude > 1e-6f && bf.sqrMagnitude > 1e-6f)
            {
                _clipRoll = Vector3.SignedAngle(cf, bf, axis);
                _clipRollAxis = axis;
                _clipRollO = cHips;
            }
        }
        // each bone's start: from the clip's (yaw, mirror and roll applied) to the body's own
        _segOff ??= new Quaternion[_bodies.Count];
        var offs = new List<string>();
        float offSum = 0f;
        int offN = 0;
        for (int i = 0; i < _bodies.Count; i++)
        {
            _segOff[i] = Quaternion.identity;
            if (_segA[i] < 0) continue;
            Vector3 c0 = ClipAim(i, 0f), b0 = _bodies[i].Rb.rotation * _segLocal[i];
            _segOff[i] = Quaternion.FromToRotation(c0, b0);
            float a = Vector3.Angle(c0, b0);
            offSum += a; offN++;
            offs.Add($"{_bodies[i].Role} {a:F0}");
        }
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: motion clip {clip.Name}{(mirror ? " (mirrored)" : "")} at {_t:F2} s, {clip.Length:F1} s long, " +
                           $"laid on the body {(upright ? "standing" : "lying")}, turned {Quaternion.Angle(Quaternion.identity, _clipYaw):F0} deg" +
                           $"{(upright ? "" : $", rolled {_clipRoll:F0} deg onto its side (fades over {RollFade:F1} s)")}; " +
                           $"bones {(offN > 0 ? offSum / offN : 0f):F0} deg from the clip's start on average ({string.Join(", ", offs)}), eased onto it over {OffFade:F1} s");

        // the clip's start in its own frame, mirrored if asked (the alignment above needs it before the yaw exists)
        Vector3 Raw(string j)
        {
            Vector3 p = clip.At(clip.Index(Side(j, mirror)), 0f);
            if (mirror) p -= 2f * Vector3.Dot(p - _clipMirO, _clipMirN) * _clipMirN;
            return p;
        }
    }

    // the muscle target of body i, swung so its bone points along the clip's
    // 0.20.0 (user log: floor clips 22-73 deg off, worst where the trunk was off the clip's, e.g. writhe_curl spine 67): limb targets
    // were the clip's bone directions in the world, which only fit a body whose trunk lies exactly as the clip's does. A body rolled
    // partway or twisted had every limb aimed somewhere that does not fit it. Legs now follow the clip relative to its hips, arms and
    // the head relative to its chest, carried onto the body's own pelvis and chest; the trunk keeps its weak guide (ClipGuide).
    private static Quaternion TrunkFrame(Vector3 up, Vector3 left)
    {
        if (up.sqrMagnitude < 1e-8f) return Quaternion.identity;
        up.Normalize();
        left = Vector3.ProjectOnPlane(left, up);
        if (left.sqrMagnitude < 1e-8f) return Quaternion.identity;
        return Quaternion.LookRotation(Vector3.Cross(up, left.normalized), up);   // Unity: forward = up x left
    }

    private void ClipFrames(float t)
    {
        if (_relAt == t) return;
        _relAt = t;
        Quaternion clipHips = TrunkFrame(ClipPos(_jSpine1, t) - ClipPos(_jHips, t), ClipPos(_jLLeg, t) - ClipPos(_jRLeg, t));
        Quaternion clipChest = TrunkFrame(ClipPos(_jNeck2, t) - ClipPos(_jChest, t), ClipPos(_jLArm, t) - ClipPos(_jRArm, t));
        var pelvis = _bodies[_pelvis].Rb;
        var chest = _bodies[_chest].Rb;
        Quaternion bodyHips = TrunkFrame(pelvis.rotation * _pelvisSegLocal, _bodies[_thighL].Rb.position - _bodies[_thighR].Rb.position);
        Quaternion bodyChest = _arms[0].Up >= 0 && _arms[1].Up >= 0
            ? TrunkFrame(chest.rotation * _chestSegLocal, _bodies[_arms[0].Up].Rb.position - _bodies[_arms[1].Up].Rb.position)
            : bodyHips * Quaternion.Inverse(clipHips) * clipChest;
        _relPelvis = bodyHips * Quaternion.Inverse(clipHips);
        _relChest = bodyChest * Quaternion.Inverse(clipChest);
    }

    // body i's clip bone at time t, carried onto the body's own trunk (legs: hips, arms and head: chest; the trunk itself as it is)
    private Vector3 ClipAim(int i, float t)
    {
        Vector3 raw = ClipDir(_segA[i], _segB[i], t);
        var role = _bodies[i].Role;
        if (role is Role.ThighL or Role.ThighR or Role.CalfL or Role.CalfR or Role.FootL or Role.FootR) { ClipFrames(t); return _relPelvis * raw; }
        if (role is Role.UpperL or Role.UpperR or Role.ForeL or Role.ForeR or Role.HandL or Role.HandR or Role.Head or Role.Neck) { ClipFrames(t); return _relChest * raw; }
        return raw;
    }

    // where body i's bone is told to point at clip time t: the clip's bone, turned from the body's own start pose for the first OffFade
    private Vector3 ClipTarget(int i, float t, Vector3 raw)
    {
        if (_segOff == null) return raw;
        float fo = 1f - Smooth(0f, OffFade, t);
        return fo > 0.001f ? Quaternion.Slerp(Quaternion.identity, _segOff[i], fo) * raw : raw;
    }

    private Quaternion ClipDes(int i, Quaternion des, float w)
    {
        if (w <= 0f || _segA == null || _segA[i] < 0) return des;
        Vector3 raw = ClipDir(_segA[i], _segB[i], ClipT);   // in the world, as the clip lies (for the log)
        Vector3 want = ClipTarget(i, ClipT, ClipAim(i, ClipT));
        Vector3 now = des * _segLocal[i];
        // user 2026-11-02 ("i dont think i really noticed them"): how far each limb really is from the clip's (log, per clip)
        if (w > 0.9f && ClipOn)
        {
            Vector3 real = _bodies[i].Rb.rotation * _segLocal[i];
            float off = Vector3.Angle(real, want), offRaw = Vector3.Angle(real, raw);
            _trackSum += off;
            _trackRawSum += offRaw;
            _trackN++;
            if (ClipT < 0.25f) { _trackStartSum += off; _trackRawStartSum += offRaw; _trackStartN++; }
            var role = _bodies[i].Role;
            _trackByRole.TryGetValue(role, out var r);
            _trackByRole[role] = (r.sum + off, r.n + 1);
        }
        return Quaternion.Slerp(des, Quaternion.FromToRotation(now, want) * des, w);
    }

    private float _trackSum, _trackStartSum, _trackRawSum, _trackRawStartSum;
    private int _trackN, _trackStartN;
    private readonly Dictionary<Role, (float sum, int n)> _trackByRole = new Dictionary<Role, (float, int)>();

    // the clip's tracking: mean angle between each limb and the clip's, overall and the worst kind of limb; reset for the next clip
    private string TrackReport()
    {
        if (_trackN == 0) return "no tracking samples";
        var parts = new List<string>();
        foreach (var kv in _trackByRole) parts.Add($"{kv.Key} {kv.Value.sum / Mathf.Max(1, kv.Value.n):F0}");
        float sn = Mathf.Max(1, _trackStartN);
        string s = $"limbs {_trackSum / _trackN:F0} deg off their targets on average, {(_trackStartN > 0 ? _trackStartSum / sn : 0f):F0} in its first 0.25 s " +
                   $"({string.Join(", ", parts)}); {_trackRawSum / _trackN:F0} deg off the clip itself, {(_trackStartN > 0 ? _trackRawStartSum / sn : 0f):F0} in its first 0.25 s; " +
                   $"clip slowed {_clipLag:F2} s to keep pace";
        _trackSum = 0f; _trackN = 0; _trackStartSum = 0f; _trackStartN = 0; _trackRawSum = 0f; _trackRawStartSum = 0f; _trackByRole.Clear();
        return s;
    }

    // Lying hurt after a death: a floor clip for the way it lies (on its back, front or side), in place of the writhing
    private void TryFloorClip(EBodyPart part)
    {
        _floorTried = true;
        // a death clip held its last frame until now; lying hurt takes over from the pose it is in (no clip: the pain poses)
        _clip = null;
        if (!Plugin.FloorClips.Value || ClipLibrary.Count == 0) return;
        var chest = _bodies[_chest].Rb;
        float faceUp = Vector3.Dot(chest.rotation * _chestFwdLocal, Vector3.up);
        string[] pick = faceUp > 0.45f
            ? (part is EBodyPart.LeftLeg or EBodyPart.RightLeg
                ? new[] { "floor_leg_wound", "floor_leg_wound", "floor_writhe_curl", "floor_roll" }
                : new[] { "floor_writhe_curl", "floor_roll", "floor_elbow_collapse", "floor_reach_out", "floor_plead", "floor_last_breath", "floor_turn_over_push_up", "floor_hide", "floor_leg_wound" })
            : faceUp < -0.45f ? new[] { "floor_crawl", "floor_crawl", "floor_turn_over_push_up" }
            : new[] { "floor_fetal_rock", "floor_cover_head", "floor_writhe_curl" };
        // only clips that start within 110 deg of the way the body lies (a body on its front cannot use one that starts on its back:
        // floor_turn_over_push_up starts on its back and was picked for bodies lying on their fronts)
        float bodyTilt = Mathf.Acos(Mathf.Clamp(faceUp, -1f, 1f)) * Mathf.Rad2Deg;
        var fits = new List<string>();
        foreach (var n in pick)
        {
            var c = ClipLibrary.Get(n);
            if (c != null && Mathf.Abs(bodyTilt - Mathf.Acos(Mathf.Clamp(c.StartFaceUp, -1f, 1f)) * Mathf.Rad2Deg) <= 110f) fits.Add(n);
        }
        var clip = fits.Count > 0 ? ClipLibrary.Get(fits[_rnd.Next(fits.Count)]) : null;
        if (ForceFloorClip != null) { clip = ClipLibrary.Get(ForceFloorClip) ?? clip; ForceFloorClip = null; }
        if (clip == null)
        {
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: no floor clip starts the way it lies (chest facing up {faceUp:F2}), pain poses");
            return;
        }
        StartClip(clip, _rnd.Next(2) == 0);
        // the clip is the lying hurt now; it ends lying still
        _hurtTo = _t + clip.Length + 0.3f;
        if (_throesTo > 0f) { float len = _throesTo - _throesFrom; _throesFrom = _hurtTo + 0.4f; _throesTo = _throesFrom + len; }
    }

#if DEV_PANEL
    // dev panel overlay: the clip's skeleton where the body is told to be (cyan left, orange right), and per body its real bone
    // (white) next to the direction its muscle is told (yellow); the label says the clip, its time, the roll and the mirror
    private static readonly string[][] DevSkel =
    {
        new[] { "Hips", "Spine1" }, new[] { "Spine1", "Chest" }, new[] { "Chest", "Neck1" }, new[] { "Neck1", "Neck2" }, new[] { "Neck2", "Head" },
        new[] { "Chest", "LeftArm" }, new[] { "LeftArm", "LeftForeArm" }, new[] { "LeftForeArm", "LeftHand" },
        new[] { "Chest", "RightArm" }, new[] { "RightArm", "RightForeArm" }, new[] { "RightForeArm", "RightHand" },
        new[] { "Hips", "LeftLeg" }, new[] { "LeftLeg", "LeftShin" }, new[] { "LeftShin", "LeftFoot" },
        new[] { "Hips", "RightLeg" }, new[] { "RightLeg", "RightShin" }, new[] { "RightShin", "RightFoot" }
    };

    internal void DevDrawClip(Action<Vector3, Vector3, Color, float> line, Action<Vector3, string, Color> label)
    {
        if (_clip == null || _t < _clipFrom || _segA == null) return;
        float t = ClipT;
        foreach (var s in DevSkel)
        {
            int a = J(s[0]), b = J(s[1]);
            if (a < 0 || b < 0) continue;
            bool right = s[1].StartsWith("Right", StringComparison.Ordinal);
            line(ClipPos(a, t), ClipPos(b, t), right ? new Color(1f, 0.55f, 0.1f, 1f) : new Color(0.2f, 0.9f, 1f, 1f), 2.5f);
        }
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_segA[i] < 0) continue;
            var rb = _bodies[i].Rb;
            float len = 0.28f;
            line(rb.position, rb.position + rb.rotation * _segLocal[i] * len, Color.white, 2f);
            line(rb.position, rb.position + ClipTarget(i, t, ClipAim(i, t)) * len, new Color(1f, 0.95f, 0.2f, 1f), 2f);
        }
        float roll = _clipRoll * (1f - Smooth(0f, RollFade, t));
        label(ClipPos(_jHips, t) + Vector3.up * 0.9f, $"{_clip.Name}{(_clipMirror ? " mirrored" : "")}  t {t:F2}/{_clip.Length:F1}  w {ClipW():F2}  roll {roll:F0}", Color.cyan);
    }
#endif

    // On the floor there is no support hand: a weak guide turns the trunk the clip's way (rolling, sitting up, turning over)
    // and draws the hips along its path (the crawl), about what a body pushing on the floor with its limbs would manage.
    private void ClipGuide(float dt, float w, float wnMax)
    {
        if (w <= 0f) return;
        float t = ClipT;
        var pelvis = _bodies[_pelvis].Rb;
        var chest = _bodies[_chest].Rb;
        Upright(pelvis, pelvis.rotation * _pelvisSegLocal, ClipDir(_jHips, _jSpine1, t), 0.45f * w, wnMax, 0.5f * _mass);
        Upright(chest, chest.rotation * _chestSegLocal, ClipDir(_jChest, _jNeck2, t), 0.45f * w, wnMax, 0.3f * _mass);
        Vector3 err = Flat3(ClipPos(_jHips, t) - pelvis.position);
        Vector3 f = _mass * (6f * err - 3f * Flat3(pelvis.velocity));
        pelvis.AddForce(Vector3.ClampMagnitude(f, _mass * 3f) * w);
        if (_clipLogs < 1 && t > _clip.Length - 0.1f)
        {
            _clipLogs++;
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: clip {_clip.Name} near its end, hips {err.magnitude:F2} m from where the clip has them, {TrackReport()}");
        }
    }
}
