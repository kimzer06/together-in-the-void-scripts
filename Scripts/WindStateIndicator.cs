using UnityEngine;

// ตัวแสดงผลสถานะ: Push=เขียว, Pull=แดง, Disabled=เทา (ตั้งเองได้)
// แปะไว้กับวัตถุที่มี Renderer (ไฟ, ปุ่ม, โลโก้)
// รองรับทั้งเปลี่ยน Material ตรง ๆ หรือใช้ MaterialPropertyBlock เปลี่ยนแค่สี

public class WindStateIndicator : MonoBehaviour, IActivatable, IWindModeActivatable
{
    [Header("Targets")]
    [SerializeField] private Renderer[] renderers;

    [Header("Use Materials (ถ้าไม่ติ๊กจะใช้ Color ผ่าน MPB)")]
    [SerializeField] private bool useMaterials = false;
    [SerializeField] private Material pushMaterial;     // เขียว
    [SerializeField] private Material pullMaterial;     // แดง
    [SerializeField] private Material disabledMaterial; // เทา

    [Header("Or Use Colors (MPB)")]
    [SerializeField] private Color pushColor = new Color(0.2f, 0.9f, 0.2f, 1f);
    [SerializeField] private Color pullColor = new Color(0.9f, 0.2f, 0.2f, 1f);
    [SerializeField] private Color disabledColor = new Color(0.5f, 0.5f, 0.5f, 1f);
    [Tooltip("ชื่อพารามิเตอร์สีใน Shader (เช่น _BaseColor, _Color)")]
    [SerializeField] private string colorProperty = "_BaseColor";

    private MaterialPropertyBlock _mpb;

    private void Awake()
    {
        if (renderers == null || renderers.Length == 0)
        {
            var r = GetComponent<Renderer>();
            if (r) renderers = new Renderer[] { r };
        }
        if (!useMaterials) _mpb = new MaterialPropertyBlock();
    }

    public void Activate(bool on)
    {
        // mapping เดิม: on=true → Push, false → Disabled (หรือแล้วแต่จะตีความ)
        SetWindMode(on ? WindMode.Push : WindMode.Disabled);
    }

    public void SetWindMode(WindMode mode)
    {
        if (useMaterials)
        {
            ApplyMaterialMode(mode);
        }
        else
        {
            ApplyColorMode(mode);
        }
    }

    private void ApplyMaterialMode(WindMode mode)
    {
        Material m = disabledMaterial;
        if (mode == WindMode.Push) m = pushMaterial ? pushMaterial : m;
        else if (mode == WindMode.Pull) m = pullMaterial ? pullMaterial : m;

        if (m == null) return;

        foreach (var r in renderers)
        {
            if (!r) continue;
            // ใช้ sharedMaterial ถ้าต้องการแชร์, หรือ r.material เพื่ออินสแตนซ์ใหม่
            r.sharedMaterial = m;
        }
    }

    private void ApplyColorMode(WindMode mode)
    {
        Color c = disabledColor;
        if (mode == WindMode.Push) c = pushColor;
        else if (mode == WindMode.Pull) c = pullColor;

        foreach (var r in renderers)
        {
            if (!r) continue;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(colorProperty, c);
            r.SetPropertyBlock(_mpb);
        }
    }
}
