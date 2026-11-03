using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace EuphoriaStylePhysics;

// user 2026-11-02 ("can we maybe start with the motion bricks?" ... "Yes, build it"): motion clips made with NVIDIA MotionBricks
// from EFT's own poses (E:\CUTarkov\motionbricks-work: library.py, export_esp.py). A clip is joint positions per frame in Unity
// space (ground at y 0); DeathMotion takes bone directions, the hips' height and speed and the trunk's tilt from it, and the
// muscles chase them while physics decides the real fall. Files: <plugin folder>/clips/*.espclip.
internal sealed class MotionClip
{
    public string Name, Desc;
    public float Fps = 30f;
    // 2026-11-03: how far the chest faces up in the first frame (1 on its back, -1 on its front, 0 standing or on its side).
    // All floor clips but the crawl start on their backs; picked for a body on its side or front, every limb was told the wrong
    // way from the start (raid log: floor_writhe_curl 80 deg off). TryFloorClip picks only clips that start near the body's way.
    public float StartFaceUp;
    public Vector3[][] Frames;   // [frame][joint]
    public Dictionary<string, int> Joint = new Dictionary<string, int>();

    public float Length => Frames.Length / Fps;
    public bool Floor => Name.StartsWith("floor_", StringComparison.Ordinal);

    // a joint's index (-1: the clip has no such joint)
    public int Index(string joint) => Joint.TryGetValue(joint, out int j) ? j : -1;

    // a joint's position at time t (linear between frames, held at the ends)
    public Vector3 At(int j, float t)
    {
        float f = Mathf.Clamp(t * Fps, 0f, Frames.Length - 1);
        int a = (int)f, b = Mathf.Min(a + 1, Frames.Length - 1);
        return Vector3.Lerp(Frames[a][j], Frames[b][j], f - a);
    }
}

internal static class ClipLibrary
{
    private static readonly Dictionary<string, MotionClip> All = new Dictionary<string, MotionClip>();
    internal static int Count => All.Count;

    internal static MotionClip Get(string name) => name != null && All.TryGetValue(name, out var c) ? c : null;

    // the loaded clips whose names start with prefix ("death_", "floor_"), sorted
    internal static List<string> Names(string prefix)
    {
        var list = new List<string>();
        foreach (var k in All.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) list.Add(k);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    internal static void Load(string pluginDir)
    {
        string dir = Path.Combine(pluginDir, "clips");
        if (!Directory.Exists(dir))
        {
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] motion clips: no folder {dir}");
            return;
        }
        foreach (var file in Directory.GetFiles(dir, "*.espclip"))
        {
            try
            {
                var c = Parse(File.ReadAllLines(file));
                All[c.Name] = c;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] motion clip {Path.GetFileName(file)} not loaded: {e.Message}"); }
        }
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] motion clips: {All.Count} loaded ({string.Join(", ", All.Keys)})");
    }

    // every joint DeathMotion reads (the bones, the trunk, the feet, the mirror plane)
    private static readonly string[] Needed =
    {
        "Hips", "Spine1", "Chest", "Neck1", "Neck2", "Head", "LeftArm", "LeftForeArm", "LeftHand", "RightArm", "RightForeArm", "RightHand",
        "LeftLeg", "LeftShin", "LeftFoot", "RightLeg", "RightShin", "RightFoot"
    };

    private static MotionClip Parse(string[] lines)
    {
        var c = new MotionClip();
        string[] joints = null;
        int n = 0, row = 0;
        var ci = CultureInfo.InvariantCulture;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (joints == null || c.Frames == null)
            {
                int sp = line.IndexOf(' ');
                string key = sp < 0 ? line : line.Substring(0, sp), val = sp < 0 ? "" : line.Substring(sp + 1);
                switch (key)
                {
                    case "espclip": if (val != "1") throw new Exception("unknown version " + val); break;
                    case "name": c.Name = val; break;
                    case "desc": c.Desc = val; break;
                    case "fps": c.Fps = float.Parse(val, ci); break;
                    case "frames": n = int.Parse(val, ci); break;
                    case "joints":
                        joints = val.Split(' ');
                        for (int j = 0; j < joints.Length; j++) c.Joint[joints[j]] = j;
                        c.Frames = new Vector3[n][];
                        break;
                }
                continue;
            }
            var v = line.Split(' ');
            if (v.Length != joints.Length * 3) throw new Exception($"frame {row}: {v.Length} values, expected {joints.Length * 3}");
            var f = new Vector3[joints.Length];
            for (int j = 0; j < joints.Length; j++)
                f[j] = new Vector3(float.Parse(v[3 * j], ci), float.Parse(v[3 * j + 1], ci), float.Parse(v[3 * j + 2], ci));
            if (row < n) c.Frames[row++] = f;
        }
        if (c.Name == null || c.Frames == null || row != n || n < 2) throw new Exception($"incomplete ({row}/{n} frames)");
        foreach (var j in Needed)
            if (!c.Joint.ContainsKey(j)) throw new Exception("no joint " + j);
        var f0 = c.Frames[0];
        Vector3 up = (f0[c.Joint["Neck2"]] - f0[c.Joint["Hips"]]).normalized;
        Vector3 left = Vector3.ProjectOnPlane(f0[c.Joint["LeftArm"]] - f0[c.Joint["RightArm"]], up);
        c.StartFaceUp = left.sqrMagnitude > 1e-8f ? Vector3.Cross(up, left.normalized).y : 0f;   // Unity: forward = up x left
        return c;
    }
}
