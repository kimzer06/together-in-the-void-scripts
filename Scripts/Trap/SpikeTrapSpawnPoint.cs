using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(NetworkObject))]
public class SpikeTrapSpawnPoint : NetworkBehaviour
{
    [Header("Override (Optional)")]
    [Tooltip("ถ้าเปิด จะใช้ระยะ/โหมดของจุดนี้แทนค่าที่อยู่บน prefab")]
    public bool overrideSettings = false;

    [Tooltip("ระยะดันขึ้นตามแกน up (เมตร)")]
    [Min(0f)] public float raiseDistance = 1.2f;

    [Tooltip("ใช้ localPosition (true) หรือ worldPosition (false)")]
    public bool useLocalSpace = true;

    [Header("Warning Visuals")]
    [Tooltip("Renderer ของ 'พื้นก้อน' ที่อยากให้เปลี่ยนเป็นสีแดง (ว่าง = สแกนหาใน children)")]
    [SerializeField] private List<Renderer> warningRenderers = new();

    [Tooltip("สีเตือน")]
    [SerializeField] private Color warningColor = Color.red;

    [Tooltip("คูณความสว่าง Emission ระหว่างเตือน (ถ้า shader รองรับ _EmissionColor)")]
    [Min(0f)][SerializeField] private float emissionBoost = 2f;

    [Tooltip("ให้กระพริบระหว่างเตือนไหม")]
    [SerializeField] private bool blinkDuringWarning = true;

    [Min(0.1f)][SerializeField] private float blinkFrequency = 4f;

    // cache
    MaterialPropertyBlock _mpb;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP/HDRP Lit
    static readonly int ColorId = Shader.PropertyToID("_Color");     // Standard/Legacy
    static readonly int EmissId = Shader.PropertyToID("_EmissionColor");

    void Reset()
    {
        TryCollectRenderers();
    }

    void Awake()
    {
        if (warningRenderers == null || warningRenderers.Count == 0)
            TryCollectRenderers();
        _mpb = new MaterialPropertyBlock();
    }

    void TryCollectRenderers()
    {
        warningRenderers = new List<Renderer>(GetComponentsInChildren<Renderer>(true));
    }

    /// <summary>
    /// เรียกจาก Server เท่านั้น: ทำให้พื้นจุดนี้เปลี่ยนเป็นสีแดง (ซิงค์ทุกเครื่อง) นาน duration วินาที
    /// </summary>
    public void PlayWarningForAll(float duration)
    {
        if (!IsServer || duration <= 0f) return;

        StopAllCoroutines();
        StartCoroutine(WarningRoutine(duration)); // เล่นที่เซิร์ฟเวอร์เอง

        PlayWarningClientRpc(duration);          // กระจายไปทุก Client
    }

    [ClientRpc]
    void PlayWarningClientRpc(float duration)
    {
        StopAllCoroutines();
        StartCoroutine(WarningRoutine(duration));
    }

    IEnumerator WarningRoutine(float duration)
    {
        float tEnd = Time.time + duration;
        while (Time.time < tEnd)
        {
            float phase = blinkDuringWarning
                ? 0.5f * (1f + Mathf.Sin(Time.time * Mathf.PI * 2f * blinkFrequency)) // 0..1
                : 1f;

            SetWarningVisual(true, phase);
            yield return null;
        }
        ClearWarningVisual();
    }

    void SetWarningVisual(bool active, float intensity01)
    {
        if (warningRenderers == null) return;

        foreach (var r in warningRenderers)
        {
            if (!r) continue;
            r.GetPropertyBlock(_mpb);

            // Base/Albedo
            Color baseC = Color.Lerp(Color.white, warningColor, active ? intensity01 : 0f);
            _mpb.SetColor(BaseColorId, baseC);
            _mpb.SetColor(ColorId, baseC);

            // Emission (ถ้า shader รองรับ)
            Color emiss = warningColor * (active ? Mathf.Lerp(1f, emissionBoost, intensity01) : 0f);
            _mpb.SetColor(EmissId, emiss);

            r.SetPropertyBlock(_mpb);
        }
    }

    void ClearWarningVisual()
    {
        if (warningRenderers == null) return;

        foreach (var r in warningRenderers)
        {
            if (!r) continue;
            r.GetPropertyBlock(_mpb);
            _mpb.Clear();                // ล้าง MPB = กลับค่าปกติของ material
            r.SetPropertyBlock(_mpb);
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.3f, 0f, 0.85f);
        Gizmos.DrawCube(transform.position, Vector3.one * 0.2f);
    }
}
