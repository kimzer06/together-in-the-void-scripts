using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using System.Linq;
using System.Collections.Generic;

/// <summary>
/// สคริปต์สำหรับสร้างพื้น Mesh จาก Spline โดยอัตโนมัติ (เอาไปทำเนินหิน/สไลด์)
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SplineMeshGenerator : MonoBehaviour
{
    [Header("Dependencies")]
    [Tooltip("ลาก SplineContainer มาใส่ตรงนี้ (หรือถ้าอยู่บน Object เดียวกันจะหาให้อัตโนมัติ)")]
    public SplineContainer splineContainer;

    [Header("Mesh Settings")]
    [Tooltip("ความกว้างของพื้น slide")]
    public float width = 4f;
    [Tooltip("ความหนาของพื้น (เพื่อไม่ให้ดูแบนไป)")]
    public float thickness = 0.5f;
    [Tooltip("จำนวนแบ่งส่วนโพลิกอนตามความยาว Spline (ยิ่งเยอะยิ่งเนียน)")]
    public int resolution = 100;
    [Tooltip("สร้าง Collider อัตโนมัติสำหรับให้ผู้เล่นเหยียบได้")]
    public bool generateCollider = true;

    [Header("Gaps (Exclude Mesh)")]
    [Tooltip("ช่วง knot index ที่ต้องการเว้นไม่ให้ generate mesh (ทำเป็นเหว)\nตัวอย่าง: (3,5) จะเว้นช่วงระหว่าง knot 3→4 และ 4→5\nหมายเหตุ: สำหรับ spline ที่ loop จะมี segment สุดท้าย (last→0) ด้วย")]
    public Vector2Int[] excludedKnotRanges;

    [Header("Mesh Caps")]
    [Tooltip("ปิดหัว/ท้ายของ mesh (ช่วยให้ collider ไม่ล่องหนตรงต้น/ปลาย)")]
    public bool generateEndCaps = true;

    [Tooltip("ปิดขอบของช่วงที่เว้น (gap) เพื่อให้ collider ไม่ล่องหนตรงขอบเหว")]
    public bool generateGapCaps = true;
    
    [Header("UV Settings")]
    [Tooltip("การวนซ้ำของ UV (ใช้สำหรับปรับความถี่ของ Texture หิน)")]
    public float uvScale = 0.5f;

    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;
    private Mesh _generatedMesh;

    private readonly List<int> _triangles = new();

    private void OnEnable()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshCollider = GetComponent<MeshCollider>();
        if (splineContainer == null) splineContainer = GetComponent<SplineContainer>();
        
        Spline.Changed += OnSplineChanged;
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
    }

    private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modificationType)
    {
        if (splineContainer != null && splineContainer.Splines.Contains(spline))
        {
            GenerateMesh();
        }
    }

    [ContextMenu("Generate Mesh Now")]
    public void GenerateMesh()
    {
        if (splineContainer == null || splineContainer.Spline == null) return;
        if (resolution <= 0) resolution = 10;

        if (_generatedMesh == null)
        {
            _generatedMesh = new Mesh();
            _generatedMesh.name = "SplineSlideMesh";
        }
        else
        {
            _generatedMesh.Clear();
        }

        // เพิ่มจำนวน Vertex สำหรับใส่ความหนา (Top ซ้าย-ขวา, Bottom ซ้าย-ขวา)
        // เพื่อให้ตาข่ายดูมี Volume และไม่เป็นแค่กระดาษแผ่นบางๆ
        int vertexCount = (resolution + 1) * 4;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        
        // Triangle 6 จุด สำหรับแต่ละควอด (Top, Left, Right, Bottom) รวม 24 Index ต่อ 1 Segment
        _triangles.Clear();

        var spline = splineContainer.Spline;
        float length = spline.GetLength();
        int knotCount = spline.Count;
        bool isClosed = spline.Closed;
        int segmentCount = GetSegmentCount(knotCount, isClosed);

        for (int i = 0; i <= resolution; i++)
        {
            float t = (float)i / resolution;
            
            SplineUtility.Evaluate(spline, t, out float3 pos, out float3 tangent, out float3 up);

            // ปรับจาก World Space กลับมาที่ Local Space เสมอ
            Transform splineTransform = splineContainer.transform;
            Vector3 worldPos = splineTransform.TransformPoint((Vector3)pos);
            Vector3 worldTangent = splineTransform.TransformDirection(((Vector3)tangent).normalized);
            Vector3 worldUp = splineTransform.TransformDirection(((Vector3)up).normalized);
            
            if (worldUp.sqrMagnitude < 0.01f) worldUp = Vector3.up;
            Vector3 worldRight = Vector3.Cross(worldUp, worldTangent).normalized;

            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            Vector3 localRight = transform.InverseTransformDirection(worldRight);
            Vector3 localUp = transform.InverseTransformDirection(worldUp);

            int baseIndex = i * 4;
            int topLeft = baseIndex;
            int topRight = baseIndex + 1;
            int bottomLeft = baseIndex + 2;
            int bottomRight = baseIndex + 3;

            // ผิวด้านบน
            vertices[topLeft] = localPos - (localRight * (width / 2f)) + (localUp * (thickness / 2f));
            vertices[topRight] = localPos + (localRight * (width / 2f)) + (localUp * (thickness / 2f));
            
            // ผิวด้านล่าง
            vertices[bottomLeft] = localPos - (localRight * (width / 2f)) - (localUp * (thickness / 2f));
            vertices[bottomRight] = localPos + (localRight * (width / 2f)) - (localUp * (thickness / 2f));

            // กำหนด Normal
            normals[topLeft] = localUp;
            normals[topRight] = localUp;
            // ให้ออกด้านข้างนิดนึงเพิ่มแสงเงา สำหรับ Bottom มองลงล่าง
            normals[bottomLeft] = (-localRight - localUp).normalized;
            normals[bottomRight] = (localRight - localUp).normalized;

            float v = (t * length) * uvScale;
            uvs[topLeft] = new Vector2(0f, v);
            uvs[topRight] = new Vector2(1f, v);
            uvs[bottomLeft] = new Vector2(0f, v);
            uvs[bottomRight] = new Vector2(1f, v);
        }

        for (int i = 0; i < resolution; i++)
        {
            bool isExcluded = segmentCount > 0 && IsExcludedByKnotRanges(i, resolution, segmentCount, knotCount, isClosed);
            if (isExcluded)
            {
                if (generateGapCaps)
                {
                    bool prevExcluded = (i > 0) && (segmentCount > 0 && IsExcludedByKnotRanges(i - 1, resolution, segmentCount, knotCount, isClosed));
                    bool nextExcluded = (i < resolution - 1) && (segmentCount > 0 && IsExcludedByKnotRanges(i + 1, resolution, segmentCount, knotCount, isClosed));

                    // Cap at start boundary: previous segment is included, current is excluded
                    if (!prevExcluded)
                        AddCapAtRing(i, flipWinding: false);

                    // Cap at end boundary: current excluded, next included
                    if (!nextExcluded)
                        AddCapAtRing(i + 1, flipWinding: true);
                }

                continue;
            }

            int currentBase = i * 4;
            int nextBase = (i + 1) * 4;

            int topLeft1 = currentBase;         int topRight1 = currentBase + 1;
            int bottomLeft1 = currentBase + 2;  int bottomRight1 = currentBase + 3;

            int topLeft2 = nextBase;            int topRight2 = nextBase + 1;
            int bottomLeft2 = nextBase + 2;     int bottomRight2 = nextBase + 3;

            // 1. หน้าด้านบน Top Face
            _triangles.Add(topLeft1); _triangles.Add(topLeft2); _triangles.Add(topRight1);
            _triangles.Add(topRight1); _triangles.Add(topLeft2); _triangles.Add(topRight2);

            // 2. หน้าด้านล่าง Bottom Face
            _triangles.Add(bottomLeft1); _triangles.Add(bottomRight1); _triangles.Add(bottomLeft2);
            _triangles.Add(bottomRight1); _triangles.Add(bottomRight2); _triangles.Add(bottomLeft2);

            // 3. หน้าด้านซ้าย Left Face
            _triangles.Add(topLeft1); _triangles.Add(bottomLeft1); _triangles.Add(topLeft2);
            _triangles.Add(bottomLeft1); _triangles.Add(bottomLeft2); _triangles.Add(topLeft2);

            // 4. หน้าด้านขวา Right Face
            _triangles.Add(topRight1); _triangles.Add(topRight2); _triangles.Add(bottomRight1);
            _triangles.Add(bottomRight1); _triangles.Add(topRight2); _triangles.Add(bottomRight2);
        }

        if (generateEndCaps && resolution >= 1)
        {
            AddCapAtRing(0, flipWinding: false);
            AddCapAtRing(resolution, flipWinding: true);
        }

        _generatedMesh.vertices = vertices;
        _generatedMesh.normals = normals;
        _generatedMesh.uv = uvs;
        _generatedMesh.triangles = _triangles.ToArray();
        _generatedMesh.RecalculateBounds();

        _meshFilter.sharedMesh = _generatedMesh;

        if (generateCollider)
        {
            if (_meshCollider == null)
            {
                _meshCollider = gameObject.AddComponent<MeshCollider>();
            }
            _meshCollider.sharedMesh = _generatedMesh;
        }
    }

    private void AddCapAtRing(int ringIndex, bool flipWinding)
    {
        int baseIndex = ringIndex * 4;
        int tl = baseIndex;
        int tr = baseIndex + 1;
        int bl = baseIndex + 2;
        int br = baseIndex + 3;

        if (!flipWinding)
        {
            _triangles.Add(tl); _triangles.Add(tr); _triangles.Add(bl);
            _triangles.Add(bl); _triangles.Add(tr); _triangles.Add(br);
        }
        else
        {
            _triangles.Add(tl); _triangles.Add(bl); _triangles.Add(tr);
            _triangles.Add(bl); _triangles.Add(br); _triangles.Add(tr);
        }
    }

    private int GetSegmentCount(int knotCount, bool closed)
    {
        if (knotCount <= 1) return 0;
        return closed ? knotCount : (knotCount - 1);
    }

    private bool IsExcludedByKnotRanges(int segmentSampleIndex, int sampleResolution, int segmentCount, int knotCount, bool closed)
    {
        if (excludedKnotRanges == null || excludedKnotRanges.Length == 0) return false;
        if (segmentCount <= 0 || knotCount <= 1) return false;

        // Use the actual normalized t-range each knot segment occupies.
        // In Unity Splines, normalized t spans segments uniformly by segment index (not by arc length),
        // so segment k corresponds to t in [k/segmentCount, (k+1)/segmentCount].
        float tMid = (segmentSampleIndex + 0.5f) / Mathf.Max(sampleResolution, 1f);
        tMid = Mathf.Clamp01(tMid);

        for (int r = 0; r < excludedKnotRanges.Length; r++)
        {
            var range = excludedKnotRanges[r];
            int aRaw = range.x;
            int bRaw = range.y;

            // Clamp raw inputs first
            int a = Mathf.Clamp(aRaw, 0, knotCount - 1);
            int b = Mathf.Clamp(bRaw, 0, knotCount - 1);

            // For open splines, wrapping doesn't make sense; normalize to increasing.
            if (!closed && a > b) (a, b) = (b, a);

            // Exclude knot segments k in [a, b-1] (open) or support wrap (closed).
            // Each segment k maps to t in [k/segmentCount, (k+1)/segmentCount].
            bool IsTInSegment(int k, float t)
            {
                float t0 = (float)k / segmentCount;
                float t1 = (float)(k + 1) / segmentCount;
                // include start, exclude end to avoid double-hitting boundaries
                return t >= t0 && t < t1;
            }

            if (!closed)
            {
                int startMin = a;
                int startMax = Mathf.Max(a, b - 1);
                startMax = Mathf.Clamp(startMax, 0, knotCount - 2);
                for (int k = startMin; k <= startMax; k++)
                {
                    if (IsTInSegment(k, tMid)) return true;
                }
            }
            else
            {
                // Closed spline: if a <= b => exclude k in [a, b-1]
                // If a > b => wrapping range, exclude [a, last] and [0, b-1]
                if (a <= b)
                {
                    int startMin = a;
                    int startMax = Mathf.Max(a, b - 1);
                    startMax = Mathf.Clamp(startMax, 0, knotCount - 1);
                    for (int k = startMin; k <= startMax; k++)
                    {
                        if (IsTInSegment(k, tMid)) return true;
                    }
                }
                else
                {
                    // wrap part 1: [a, knotCount-1]
                    for (int k = a; k <= knotCount - 1; k++)
                    {
                        if (IsTInSegment(k, tMid)) return true;
                    }
                    // wrap part 2: [0, b-1]
                    int endMax = Mathf.Clamp(b - 1, -1, knotCount - 1);
                    for (int k = 0; k <= endMax; k++)
                    {
                        if (IsTInSegment(k, tMid)) return true;
                    }
                }
            }
        }

        return false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (splineContainer == null || splineContainer.Spline == null) return;
        if (excludedKnotRanges == null || excludedKnotRanges.Length == 0) return;

        var spline = splineContainer.Spline;
        int knotCount = spline.Count;
        if (knotCount <= 1) return;

        bool closed = spline.Closed;
        int segmentCount = GetSegmentCount(knotCount, closed);
        if (segmentCount <= 0) return;

        Transform st = splineContainer.transform;
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.85f);

        for (int r = 0; r < excludedKnotRanges.Length; r++)
        {
            var range = excludedKnotRanges[r];
            int a = range.x;
            int b = range.y;
            if (a > b) (a, b) = (b, a);
            a = Mathf.Clamp(a, 0, knotCount - 1);
            b = Mathf.Clamp(b, 0, knotCount - 1);

            // draw each excluded segment k (k->k+1) for k in [a, b-1]
            int startMin = a;
            int startMax = Mathf.Max(a, b - 1);
            startMax = closed
                ? Mathf.Clamp(startMax, 0, knotCount - 1)
                : Mathf.Clamp(startMax, 0, knotCount - 2);

            for (int k = startMin; k <= startMax; k++)
            {
                // approximate with samples along the segment portion of normalized t
                float t0 = (float)k / segmentCount;
                float t1 = (float)(k + 1) / segmentCount;
                int steps = 12;
                Vector3 prev = default;
                for (int s = 0; s <= steps; s++)
                {
                    float tt = Mathf.Lerp(t0, t1, (float)s / steps);
                    SplineUtility.Evaluate(spline, tt, out float3 pos, out _, out _);
                    Vector3 wp = st.TransformPoint((Vector3)pos);
                    if (s > 0) Gizmos.DrawLine(prev, wp);
                    prev = wp;
                }

                // small marker at middle
                float tMid = (t0 + t1) * 0.5f;
                SplineUtility.Evaluate(spline, tMid, out float3 midPos, out _, out _);
                Gizmos.DrawSphere(st.TransformPoint((Vector3)midPos), 0.15f);
            }
        }
    }
#endif

#if UNITY_EDITOR
    private void OnValidate()
    {
        // ให้อัพเดตทันทีที่ปรับค่าใน Inspector
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this && gameObject.activeInHierarchy) GenerateMesh();
        };
    }
#endif
}
