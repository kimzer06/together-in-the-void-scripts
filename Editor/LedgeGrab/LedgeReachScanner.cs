using System.Collections.Generic;
using System.Text;
using StarterAssets;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// หาขอบที่ "เกาะขอบทำให้ขึ้นถึงใหม่" ในซีนที่เปิดอยู่ — สูงกว่าที่กระโดด + double jump ขึ้นถึง แต่ไม่เกินที่มือเอื้อมจับได้
/// ใช้ไล่หาจุดที่ผู้เล่นอาจปีนข้ามพัซเซิลที่ออกแบบด้วยความสูงกระโดด แล้ววาง NoLedgeGrab
///
/// วิธี: ยิง ray ลงเป็นตาราง → แผนที่ความสูงผิวบนสุด → ช่องที่สูงกว่าช่องข้างๆ อยู่ในช่วงนั้น + บนขอบมีที่ยืน = จุดที่ขึ้นถึงใหม่
/// ข้อจำกัด: เห็นแค่ผิวบนสุดจากฟ้า (ใต้หลังคา/ในอาคารไม่นับ) · ไม่คิดลม/ลิฟต์/ของที่ขยับ/แดชข้ามช่อง · ผลแสดงใน Scene view จนสั่ง Clear
/// </summary>
public static class LedgeReachScanner
{
    const string Menu = "Tools/Together/Ledge Grab/";
    const string PrefabA = "Assets/_Together In the void/Player/T_PlayerA.prefab";
    const string PrefabB = "Assets/_Together In the void/Player/T_PlayerB.prefab";

    /// <summary>
    /// เท้าขึ้นได้สูงสุดจากพื้นที่ยืน (เมตร) ด้วยกระโดด + double jump ที่จุดสูงสุด — วัดจาก Solo Play 2026-10-16 (Lyra/Bobby เท่ากัน 3.44)
    /// + ก้น capsule ขึ้นขอบได้อีกนิดตอนชนสัน
    /// </summary>
    const float PlainReach = 3.55f;
    const int MaxCells = 6_000_000;
    const int MaxMarkers = 20000;

    struct Marker
    {
        public Vector3 top;
        public Vector3 bottom;
        public bool lyraOnly;
    }

    static readonly List<Marker> Markers = new List<Marker>();

    [MenuItem(Menu + "Scan New Reach In Open Scenes")]
    static void Scan()
    {
        var a = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabA)?.GetComponent<ThirdPersonController_Rigidbody>();
        var b = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabB)?.GetComponent<ThirdPersonController_Rigidbody>();
        if (a == null || b == null || a.LedgeClimbRootMotion == null || b.LedgeClimbRootMotion == null)
        {
            EditorUtility.DisplayDialog("Ledge Reach", "ไม่เจอ T_PlayerA/B หรือยังไม่ได้ใส่ LedgeClimbRootMotion", "OK");
            return;
        }

        float reachA = PlainReach + a.LedgeClimbRootMotion.TotalOffset.y + a.LedgeGrabSnapUp;
        float reachB = PlainReach + b.LedgeClimbRootMotion.TotalOffset.y + b.LedgeGrabSnapUp;
        float reachMax = Mathf.Max(reachA, reachB);
        float reachMin = Mathf.Min(reachA, reachB);
        bool aIsTaller = reachA >= reachB;
        int mask = a.LedgeLayers.value | b.LedgeLayers.value;
        float minNormalY = Mathf.Min(a.GroundNormalMinY, b.GroundNormalMinY);

        Physics.SyncTransforms();
        var blockers = Object.FindObjectsByType<NoLedgeGrab>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        // ขอบเขต = collider ทึบทั้งหมดใน layer ที่เกาะได้
        bool any = false;
        Bounds world = default;
        foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (c.isTrigger || !c.enabled || (mask & (1 << c.gameObject.layer)) == 0) continue;
            if (!any) { world = c.bounds; any = true; }
            else world.Encapsulate(c.bounds);
        }
        if (!any)
        {
            EditorUtility.DisplayDialog("Ledge Reach", "ไม่มี collider ใน layer ที่เกาะได้", "OK");
            return;
        }

        float cell = 0.5f;
        while ((world.size.x / cell) * (world.size.z / cell) > MaxCells) cell *= 2f;
        int nx = Mathf.CeilToInt(world.size.x / cell) + 1;
        int nz = Mathf.CeilToInt(world.size.z / cell) + 1;
        float topY = world.max.y + 1f;
        float rayLen = world.size.y + 2f;

        var height = new float[nx, nz];
        var col = new Collider[nx, nz];
        try
        {
            for (int i = 0; i < nx; i++)
            {
                if (i % 20 == 0 && EditorUtility.DisplayCancelableProgressBar("Ledge Reach", $"ยิง ray {i}/{nx}", (float)i / nx))
                    return;
                for (int j = 0; j < nz; j++)
                {
                    var origin = new Vector3(world.min.x + i * cell, topY, world.min.z + j * cell);
                    if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLen, mask, QueryTriggerInteraction.Ignore)
                        && hit.normal.y >= minNormalY && !ThirdPersonController_Rigidbody.IsInvisibleBlocker(hit.collider))
                    {
                        height[i, j] = hit.point.y;
                        col[i, j] = hit.collider;
                    }
                    else height[i, j] = float.NaN;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Markers.Clear();
        var groups = new Dictionary<string, (int both, int lyra, float minD, float maxD, Vector3 sample)>();
        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };
        for (int i = 1; i < nx - 1; i++)
        {
            for (int j = 1; j < nz - 1; j++)
            {
                float h = height[i, j];
                if (float.IsNaN(h)) continue;
                for (int k = 0; k < 4; k++)
                {
                    float hn = height[i + dx[k], j + dz[k]];
                    if (float.IsNaN(hn)) continue;
                    float d = h - hn;
                    if (d <= PlainReach || d > reachMax) continue;

                    // ต้องมีที่ยืนบนขอบ (ช่องถัดเข้าไปสูงพอๆ กัน) — ผนังบางจับไม่ได้อยู่แล้ว
                    float hin = height[i - dx[k], j - dz[k]];
                    if (float.IsNaN(hin) || Mathf.Abs(hin - h) > 0.25f) continue;

                    var edge = new Vector3(world.min.x + (i + dx[k] * 0.5f) * cell, h, world.min.z + (j + dz[k] * 0.5f) * cell);
                    bool blocked = false;
                    foreach (var bl in blockers) if (bl.Blocks(edge, col[i, j])) { blocked = true; break; }
                    if (blocked) continue;

                    bool tallOnly = d > reachMin;
                    if (Markers.Count < MaxMarkers)
                        Markers.Add(new Marker { top = edge, bottom = new Vector3(edge.x, hn, edge.z), lyraOnly = tallOnly });

                    string key = PathOf(col[i, j] != null ? col[i, j].transform : null);
                    groups.TryGetValue(key, out var g);
                    if (g.both + g.lyra == 0) { g.minD = d; g.maxD = d; g.sample = edge; }
                    if (tallOnly) g.lyra++; else g.both++;
                    g.minD = Mathf.Min(g.minD, d);
                    g.maxD = Mathf.Max(g.maxD, d);
                    groups[key] = g;
                }
            }
        }

        var sb = new StringBuilder();
        string tallName = aIsTaller ? "Lyra" : "Bobby";
        sb.AppendLine($"[LedgeReach] {SceneManager.GetActiveScene().name}: ช่อง {cell} ม. · กระโดดถึง {PlainReach:0.00} · เกาะถึง Lyra {reachA:0.00} / Bobby {reachB:0.00} · {groups.Count} วัตถุ · NoLedgeGrab {blockers.Length} ตัว");
        var list = new List<KeyValuePair<string, (int both, int lyra, float minD, float maxD, Vector3 sample)>>(groups);
        list.Sort((x, y) => (y.Value.both + y.Value.lyra).CompareTo(x.Value.both + x.Value.lyra));
        foreach (var kv in list)
        {
            var g = kv.Value;
            sb.AppendLine($"  {kv.Key} — ทั้งคู่ {g.both} · {tallName}เท่านั้น {g.lyra} · สูง {g.minD:0.0}–{g.maxD:0.0} ม. · เช่น {g.sample.x:0.0}, {g.sample.y:0.0}, {g.sample.z:0.0}");
        }
        // รายการยาวเกิน Console — เขียนไฟล์ไว้ใน Temp ของโปรเจกต์ (git ไม่เก็บ)
        string file = $"Temp/LedgeReachScan_{SceneManager.GetActiveScene().name}.txt";
        System.IO.File.WriteAllText(file, sb.ToString());
        Debug.Log(sb + $"\n(ฉบับเต็ม: {file})");

        SceneView.duringSceneGui -= Draw;
        if (Markers.Count > 0) SceneView.duringSceneGui += Draw;
        SceneView.RepaintAll();
    }

    [MenuItem(Menu + "Clear Scan Markers")]
    static void Clear()
    {
        Markers.Clear();
        SceneView.duringSceneGui -= Draw;
        SceneView.RepaintAll();
    }

    static void Draw(SceneView view)
    {
        foreach (var m in Markers)
        {
            Handles.color = m.lyraOnly ? new Color(0.75f, 0.35f, 1f) : new Color(1f, 0.55f, 0.1f);
            Handles.DrawLine(m.bottom, m.top);
            Handles.DrawWireCube(m.top, Vector3.one * 0.15f);
        }
    }

    static string PathOf(Transform t)
    {
        if (t == null) return "(ไม่มี)";
        // root / ลูกชั้นแรก / … / ตัวที่โดน — ของในด่านจัดกลุ่มตาม root (+ PUZZLE xx) อยู่แล้ว
        var chain = new List<string>();
        for (var p = t; p != null; p = p.parent) chain.Insert(0, p.name);
        if (chain.Count <= 3) return string.Join("/", chain);
        return chain[0] + "/" + chain[1] + "/…/" + chain[chain.Count - 1];
    }
}
