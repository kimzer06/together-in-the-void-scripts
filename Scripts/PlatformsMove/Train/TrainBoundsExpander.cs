using UnityEngine;

/// <summary>
/// ขยาย Bounding Box ของ Renderer ทุกตัวใน hierarchy
/// เพื่อป้องกัน Frustum Culling ที่ขอบกล้อง (พบเฉพาะใน Build)
///
/// วิธีใช้: แปะไว้ที่ Root ของ Prefab รถไฟ
/// </summary>
public class TrainBoundsExpander : MonoBehaviour
{
    [Tooltip("ขนาดที่จะขยาย bounds ออกจากแต่ละด้าน (หน่วย world unit)")]
    [SerializeField] private float boundsExpansion = 50f;

    private void Start()
    {
        ExpandBounds();
    }

    private void OnEnable()
    {
        ExpandBounds();
    }

    private void ExpandBounds()
    {
        var renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var rend in renderers)
        {
            // ใช้ MeshFilter เพื่อแก้ bound ที่ต้นทาง (mesh level)
            if (rend is MeshRenderer meshRend)
            {
                var meshFilter = rend.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    // ขยาย bounds ของ mesh เอง
                    var mesh = meshFilter.sharedMesh;
                    var bounds = mesh.bounds;
                    bounds.Expand(boundsExpansion);
                    mesh.bounds = bounds;
                }
            }
            else if (rend is SkinnedMeshRenderer skinnedRend)
            {
                var bounds = skinnedRend.localBounds;
                bounds.Expand(boundsExpansion);
                skinnedRend.localBounds = bounds;
            }
        }
    }
}
