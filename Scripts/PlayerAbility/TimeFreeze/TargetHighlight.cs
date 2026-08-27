using UnityEngine;

[DisallowMultipleComponent]
public class TargetHighlight : MonoBehaviour
{
    [Header("AIM → Outline (QuickOutline)")]
    public Color aimOutlineColor = new(1f, 0.82f, 0.2f, 1f);
    [Range(0f, 10f)] public float aimOutlineWidth = 3f;
    [Range(0f, 10f)] public float aimPulseWidthAmplitude = 1.0f;
    [Range(0f, 20f)] public float aimPulseSpeed = 6.0f;

    [Header("FREEZE → Emission Only")]
    [ColorUsage(true, true)] public Color freezeGlowColor = new(1f, 0.82f, 0.2f, 1f);
    [Range(0f, 10f)] public float freezeGlowIntensity = 3.0f;
    public bool useGammaFix = true;

    [Header("FREEZE (Timed) → Pulse Settings")]
    [Tooltip("ความเร็วกระพริบต่ำสุด (ตอนเพิ่งเริ่มนับเวลา เหลือเวลาเยอะ)")]
    [Range(0.1f, 20f)] public float timedPulseSpeedMin = 2f;
    [Tooltip("ความเร็วกระพริบสูงสุด (เมื่อใกล้หมดเวลา)")]
    [Range(0.1f, 40f)] public float timedPulseSpeedMax = 14f;
    [Tooltip("สัดส่วนความสว่างขั้นต่ำระหว่างพัลส์ (0-1)")]
    [Range(0f, 1f)] public float timedPulseMinBrightness = 0.2f;

    Renderer[] rends;
    Outline[] outlines;
    Outline rootOutline;
    bool aimActive;

    // Forced states
    bool forcedActive;           // โหมดบังคับ (Freeze เปิดอยู่)
    bool forcedPulseActive;      // ใช้เฉพาะ Timed (กระพริบ)
    float forcedNormRemaining = 1f; // 1 = เพิ่งเริ่ม (ช้า), 0 = ใกล้หมด (เร็ว)
    float pulseT;                // เฟสของพัลส์

    Material[][] matsCache;

    static readonly int EmissionColor_Std = Shader.PropertyToID("_EmissionColor");
    static readonly int EmissiveColor_HDRP = Shader.PropertyToID("_EmissiveColor");
    static readonly int EmissiveIntensity_HDRP = Shader.PropertyToID("_EmissiveIntensity");
    const string KW_EMISSION = "_EMISSION";
    const string KW_HDRP_EMISSIVE = "_EMISSIVE_COLOR";

    void Awake()
    {
        rends = GetComponentsInChildren<Renderer>(true);

        outlines = GetComponentsInChildren<Outline>(true);
        if (outlines == null || outlines.Length == 0)
        {
            rootOutline = gameObject.AddComponent<Outline>();
            outlines = new[] { rootOutline };
        }
        else
        {
            rootOutline = GetComponent<Outline>();
            if (!rootOutline) rootOutline = outlines[0];
        }

        foreach (var ol in outlines)
        {
            if (!ol) continue;
            ol.OutlineMode = Outline.Mode.OutlineVisible;
            ol.OutlineColor = aimOutlineColor;
            ol.OutlineWidth = 0f;
            ol.enabled = false;
        }

        CacheAndPrepareMaterials();
        ClearEmission();
    }

    void Update()
    {
        if (forcedActive)
        {
            if (forcedPulseActive)
            {
                // กระพริบ: ความเร็วเพิ่มขึ้นเมื่อเวลาใกล้หมด
                float spd = Mathf.Lerp(timedPulseSpeedMin, timedPulseSpeedMax, 1f - Mathf.Clamp01(forcedNormRemaining));
                pulseT += Time.deltaTime * Mathf.Max(0.01f, spd);

                // 0..1 ไปกลับ ด้วย PingPong
                float osc = Mathf.PingPong(pulseT, 1f);

                // ความสว่าง = min..1
                float brightness = Mathf.Lerp(timedPulseMinBrightness, 1f, osc);
                ApplyEmission(freezeGlowColor, freezeGlowIntensity * brightness);
            }
            else
            {
                // ติดค้าง (โหมด Toggle)
                ApplyEmission(freezeGlowColor, freezeGlowIntensity);
            }
            return;
        }

        if (aimActive)
        {
            if (rootOutline && !rootOutline.enabled) rootOutline.enabled = true;
            if (rootOutline)
            {
                rootOutline.OutlineColor = aimOutlineColor;
                float w = aimOutlineWidth + Mathf.Sin(Time.time * aimPulseSpeed) * 0.5f * aimPulseWidthAmplitude;
                rootOutline.OutlineWidth = Mathf.Max(0f, w);
            }
        }
        else
        {
            SoftDisableOutlineAll();
            ClearEmission();
        }
    }

    public void SetAimActive(bool on)
    {
        aimActive = on;

        if (!on && !forcedActive)
        {
            HardDisableOutlineAll();
            ClearEmission();
        }
    }

    /// <summary>โหมด Freeze แบบติดค้าง (Toggle)</summary>
    public void SetForced(bool on)
    {
        forcedActive = on;
        forcedPulseActive = false; // โหมดติดค้าง ไม่กระพริบ

        if (on)
        {
            HardDisableOutlineAll();
            EnsureEmissionKeywordsEnabled(true); // realtime GI ตอน Freeze
            ApplyEmission(freezeGlowColor, freezeGlowIntensity);
        }
        else
        {
            if (!aimActive)
            {
                ClearEmission();
                SoftDisableOutlineAll();
            }
        }
    }

    /// <summary>โหมด Freeze แบบ Timed: อัปเดตภาพกระพริบด้วยค่าเวลาคงเหลือ normalized (1→ช้า, 0→เร็ว)</summary>
    public void SetForcedTimedVisual(float normalizedRemaining)
    {
        forcedActive = true;
        forcedPulseActive = true;

        // เก็บค่า norm ไว้ขับความเร็ว
        forcedNormRemaining = Mathf.Clamp01(normalizedRemaining);

        HardDisableOutlineAll();
        EnsureEmissionKeywordsEnabled(true);

        // ไม่ต้อง ApplyEmission ที่นี่ เพราะ Update() จะควบคุมกระพริบทุกเฟรม
        // รีเซ็ตเฟสบ้างเมื่อเปลี่ยนทิศทางเร็วมาก
        if (forcedNormRemaining <= 0.001f && pulseT > 10f) pulseT = 0f;
    }

    public void ForceClearAll()
    {
        aimActive = false;
        forcedActive = false;
        forcedPulseActive = false;
        forcedNormRemaining = 1f;
        pulseT = 0f;

        HardDisableOutlineAll();
        ClearEmission();
    }

    void SoftDisableOutlineAll()
    {
        if (outlines == null) return;
        foreach (var ol in outlines)
        {
            if (!ol) continue;
            if (ol.enabled)
            {
                ol.OutlineWidth = 0f;
                ol.enabled = false;
            }
        }
    }

    void HardDisableOutlineAll()
    {
        if (outlines == null) return;
        foreach (var ol in outlines)
        {
            if (!ol) continue;
            ol.OutlineWidth = 0f;
            ol.enabled = false;
        }
    }

    void CacheAndPrepareMaterials()
    {
        if (rends == null) return;

        matsCache = new Material[rends.Length][];
        for (int ri = 0; ri < rends.Length; ri++)
        {
            var r = rends[ri];
            if (!r) { matsCache[ri] = System.Array.Empty<Material>(); continue; }

            var mats = r.materials; // instance ต่อ Renderer
            matsCache[ri] = mats;

            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (!m) continue;
                m.EnableKeyword(KW_EMISSION);
                m.EnableKeyword(KW_HDRP_EMISSIVE);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.AnyEmissive;
            }
        }
    }

    void EnsureEmissionKeywordsEnabled(bool realtimeGI = false)
    {
        if (matsCache == null) return;
        for (int ri = 0; ri < matsCache.Length; ri++)
        {
            var mats = matsCache[ri];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (!m) continue;
                m.EnableKeyword(KW_EMISSION);
                m.EnableKeyword(KW_HDRP_EMISSIVE);
                m.globalIlluminationFlags = realtimeGI
                    ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                    : MaterialGlobalIlluminationFlags.AnyEmissive;
            }
        }
    }

    void ApplyEmission(Color color, float intensity)
    {
        if (matsCache == null) return;

        float inten = useGammaFix ? Mathf.LinearToGammaSpace(intensity) : intensity;

        for (int ri = 0; ri < matsCache.Length; ri++)
        {
            var mats = matsCache[ri];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (!m) continue;

                if (m.HasProperty(EmissionColor_Std))
                    m.SetColor(EmissionColor_Std, color * inten);

                if (m.HasProperty(EmissiveColor_HDRP))
                {
                    m.SetColor(EmissiveColor_HDRP, color);
                    if (m.HasProperty(EmissiveIntensity_HDRP))
                        m.SetFloat(EmissiveIntensity_HDRP, inten);
                }
            }
        }
    }

    void ClearEmission()
    {
        if (matsCache == null) return;

        for (int ri = 0; ri < matsCache.Length; ri++)
        {
            var mats = matsCache[ri];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (!m) continue;

                if (m.HasProperty(EmissionColor_Std))
                    m.SetColor(EmissionColor_Std, Color.black);

                if (m.HasProperty(EmissiveColor_HDRP))
                    m.SetColor(EmissiveColor_HDRP, Color.black);

                if (m.HasProperty(EmissiveIntensity_HDRP))
                    m.SetFloat(EmissiveIntensity_HDRP, 0f);
            }
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        aimOutlineWidth = Mathf.Max(0f, aimOutlineWidth);
        freezeGlowIntensity = Mathf.Max(0f, freezeGlowIntensity);
        timedPulseSpeedMin = Mathf.Max(0.1f, timedPulseSpeedMin);
        timedPulseSpeedMax = Mathf.Max(timedPulseSpeedMin, timedPulseSpeedMax);
    }
#endif
}