using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

[RequireComponent(typeof(NetworkObject))]
public class DisappearPlatform : NetworkBehaviour
{
    // ---------- Detection ----------
    private enum DetectionShape { Box, Sphere, Capsule, Mesh }

    [Header("Pivot & Detection (detectPosition เป็น local ของ pivot)")]
    [SerializeField] private Transform pivot;
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;

    [Header("Box")]
    [SerializeField] private Vector3 boxSize = new Vector3(1, 0.3f, 1);

    [Header("Sphere")]
    [SerializeField] private float sphereRadius = 0.6f;

    [Header("Capsule")]
    [SerializeField] private float capsuleRadius = 0.5f;
    [SerializeField] private float capsuleHeight = 2f;

    [Header("Filter")]
    [SerializeField] private LayerMask targetLayers = ~0;     // เลเยอร์ที่นับว่า "เหยียบ"
    [SerializeField] private string requiredTag = "";         // เว้นว่าง = ไม่บังคับ

    [Header("Gizmos (Preview Only)")]
    [SerializeField] private Mesh meshPreview;
    [SerializeField] private Color gizmoColor = new Color(0f, 1f, 0f, 0.25f);

    // ---------- Behaviour ----------
    private enum State : byte { Idle, Warmup, Hiding, Hidden, Cooldown, Appearing }

    [Header("Timers (seconds)")]
    [SerializeField] private float warmupSeconds = 2f;     // เหยียบแล้วเริ่มนับ 2 วิ
    [SerializeField] private float fadeOutSeconds = 0.5f;  // เวลาเฟดหาย
    [SerializeField] private float cooldownSeconds = 10f;  // หายแล้วรอ 10 วิ
    [SerializeField] private float fadeInSeconds = 0.35f;  // เวลาเฟดกลับมา

    [Header("Render & Collider")]
    [SerializeField] private List<Renderer> renderers = new(); // ปล่อยว่างได้ (auto-collect)
    [SerializeField] private List<Collider> colliders = new(); // ปล่อยว่างได้ (auto-collect)
    [SerializeField] private bool disableCollidersWhenHidden = true;
    [Tooltip("ถ้าวัสดุไม่มีอัลฟ่า ใช้สเกลแทนการเฟด")]
    [SerializeField] private bool useScaleFallback = false;

    // ---------- Netcode sync ----------
    private readonly NetworkVariable<State> _stateNV =
        new NetworkVariable<State>(State.Idle, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<double> _stateStartTimeNV =
        new NetworkVariable<double>(0,      NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ---------- Render runtime ----------
    private struct RendData
    {
        public Renderer rend;
        public MaterialPropertyBlock mpb;
        public Color baseColor;
        public bool useColorProp;
    }
    private readonly List<RendData> _rendData = new();
    private Vector3 _initialScale;
    private Coroutine _fadeCo;

    // ---------- Unity ----------
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!pivot) pivot = transform;
        _initialScale = transform.localScale;

        if (renderers.Count == 0) renderers.AddRange(GetComponentsInChildren<Renderer>(true));
        if (colliders.Count == 0) colliders.AddRange(GetComponentsInChildren<Collider>(true));

        PrepareRenderers();
        SetVisibleInstant(true);
        if (disableCollidersWhenHidden) SetCollidersEnabled(true);

        _stateNV.OnValueChanged += OnStateChanged;
    }

    private void OnDestroy()
    {
        _stateNV.OnValueChanged -= OnStateChanged;
    }

    private void Update()
    {
        if (IsServer) Server_Tick();
    }

    // ---------- Server FSM ----------
    private readonly Collider[] hits = new Collider[16];

    private void Server_Tick()
    {
        // เริ่มวอร์มอัพเมื่อ Idle และมีคนเหยียบ
        if (_stateNV.Value == State.Idle && Server_IsSomeoneOn())
        {
            Server_SwitchState(State.Warmup);
            return;
        }

        double now = NetworkManager.Singleton.ServerTime.Time;
        double elapsed = now - _stateStartTimeNV.Value;

        switch (_stateNV.Value)
        {
            case State.Warmup:
                if (elapsed >= warmupSeconds) Server_SwitchState(State.Hiding);
                break;

            case State.Hiding:
                if (elapsed >= fadeOutSeconds)
                {
                    if (disableCollidersWhenHidden) SetCollidersEnabled(false);
                    Server_SwitchState(State.Hidden);
                }
                break;

            case State.Hidden:
                // เข้าคูลดาวน์ทันทีหลังซ่อน
                Server_SwitchState(State.Cooldown);
                break;

            case State.Cooldown:
                if (elapsed >= cooldownSeconds)
                {
                    if (disableCollidersWhenHidden) SetCollidersEnabled(true);
                    Server_SwitchState(State.Appearing);
                }
                break;

            case State.Appearing:
                if (elapsed >= fadeInSeconds) Server_SwitchState(State.Idle);
                break;
        }
    }

    private void Server_SwitchState(State s)
    {
        _stateNV.Value = s;
        _stateStartTimeNV.Value = NetworkManager.Singleton.ServerTime.Time;

        // อัปเดตภาพบนเซิร์ฟเวอร์ทันที
        ApplyStateVisualImmediate(s);
    }

    private bool Server_IsSomeoneOn()
    {
        var piv = pivot ? pivot : transform;
        Vector3 center = piv.TransformPoint(detectPosition);
        Quaternion rot = piv.rotation;

        int count = 0;
        switch (detectionShape)
        {
            case DetectionShape.Box:
                Vector3 half = Vector3.Scale(boxSize, AbsVec3(piv.lossyScale)) * 0.5f;
                count = Physics.OverlapBoxNonAlloc(center, half, hits, rot, targetLayers, QueryTriggerInteraction.Ignore);
                break;
            case DetectionShape.Sphere:
                float r = sphereRadius * MaxAxis(piv.lossyScale);
                count = Physics.OverlapSphereNonAlloc(center, r, hits, targetLayers, QueryTriggerInteraction.Ignore);
                break;
            case DetectionShape.Capsule:
                float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = piv.up * (hh * MaxAxis(piv.lossyScale));
                float rad = capsuleRadius * MaxAxis(piv.lossyScale);
                count = Physics.OverlapCapsuleNonAlloc(center + up, center - up, rad, hits, targetLayers, QueryTriggerInteraction.Ignore);
                break;
            case DetectionShape.Mesh:
                count = 0; // แค่พรีวิว
                break;
        }
        if (count <= 0) return false;

        if (!string.IsNullOrEmpty(requiredTag))
        {
            for (int i = 0; i < count; i++)
            {
                var h = hits[i]; if (!h) continue;
                if (h.CompareTag(requiredTag)) return true;
                if (h.attachedRigidbody && h.attachedRigidbody.CompareTag(requiredTag)) return true;
            }
            return false;
        }
        return true;
    }

    // ---------- Visual sync ----------
    private void OnStateChanged(State oldS, State newS)
    {
        ApplyStateVisualImmediate(newS);

        if (_fadeCo != null) StopCoroutine(_fadeCo);
        _fadeCo = StartCoroutine(Co_FollowFades(newS));
    }

    private IEnumerator Co_FollowFades(State s)
    {
        double now = NetworkManager.Singleton.ServerTime.Time;
        double elapsed = now - _stateStartTimeNV.Value;

        switch (s)
        {
            case State.Hiding:
            {
                float remain = Mathf.Max(0f, fadeOutSeconds - (float)elapsed);
                yield return FadeVisible(false, remain);
                break;
            }
            case State.Hidden:
                SetRenderersEnabled(false);
                break;

            case State.Cooldown:
            {
                float remain = Mathf.Max(0f, cooldownSeconds - (float)elapsed);
                yield return Wait(remain);
                break;
            }
            case State.Appearing:
            {
                float remain = Mathf.Max(0f, fadeInSeconds - (float)elapsed);
                SetRenderersEnabled(true);
                yield return FadeVisible(true, remain);
                break;
            }
            case State.Idle:
                SetRenderersEnabled(true);
                break;
        }
    }

    private void ApplyStateVisualImmediate(State s)
    {
        switch (s)
        {
            case State.Idle:
            case State.Warmup:
                SetVisibleInstant(true);
                if (disableCollidersWhenHidden) SetCollidersEnabled(true);
                break;

            case State.Hiding:
                // เฟดจะทำในคอร์รัวทีน
                break;

            case State.Hidden:
            case State.Cooldown:
                SetVisibleInstant(false);
                if (disableCollidersWhenHidden) SetCollidersEnabled(false);
                break;

            case State.Appearing:
                // เฟดจะทำในคอร์รัวทีน
                break;
        }
    }

    // ---------- Render helpers ----------
    private void PrepareRenderers()
    {
        _rendData.Clear();
        foreach (var r in renderers)
        {
            if (!r) continue;
            var d = new RendData { rend = r, mpb = new MaterialPropertyBlock() };
            r.GetPropertyBlock(d.mpb);

            bool hasBase = r.sharedMaterial && r.sharedMaterial.HasProperty("_BaseColor");
            bool hasColor = r.sharedMaterial && r.sharedMaterial.HasProperty("_Color");
            d.useColorProp = hasBase || hasColor;
            d.baseColor   = (hasBase ? r.sharedMaterial.GetColor("_BaseColor")
                                     : hasColor ? r.sharedMaterial.GetColor("_Color") : Color.white);
            _rendData.Add(d);
        }
    }

    private void SetVisibleInstant(bool visible)
    {
        if (useScaleFallback)
        {
            transform.localScale = visible ? _initialScale : Vector3.zero;
            SetRenderersEnabled(visible);
            return;
        }

        if (visible) SetRenderersEnabled(true);

        foreach (var d in _rendData)
        {
            if (!d.rend || !d.useColorProp) continue;
            var col = d.baseColor; col.a = visible ? d.baseColor.a : 0f;
            if (d.rend.sharedMaterial.HasProperty("_BaseColor")) d.mpb.SetColor("_BaseColor", col);
            else if (d.rend.sharedMaterial.HasProperty("_Color")) d.mpb.SetColor("_Color", col);
            d.rend.SetPropertyBlock(d.mpb);
        }

        if (!visible) SetRenderersEnabled(false);
    }

    private IEnumerator FadeVisible(bool visible, float seconds)
    {
        if (seconds <= 0f) { SetVisibleInstant(visible); yield break; }

        if (useScaleFallback)
        {
            Vector3 from = transform.localScale;
            Vector3 to = visible ? _initialScale : Vector3.zero;
            float t = 0f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                transform.localScale = Vector3.Lerp(from, to, Mathf.Clamp01(t / seconds));
                yield return null;
            }
            transform.localScale = to;
            yield break;
        }

        if (visible) SetRenderersEnabled(true);

        float time = 0f;
        while (time < seconds)
        {
            time += Time.deltaTime;
            float k = Mathf.Clamp01(time / seconds);
            float a = visible ? k : (1f - k);

            foreach (var d in _rendData)
            {
                if (!d.rend || !d.useColorProp) continue;
                var col = d.baseColor; col.a = a * d.baseColor.a;
                if (d.rend.sharedMaterial.HasProperty("_BaseColor")) d.mpb.SetColor("_BaseColor", col);
                else if (d.rend.sharedMaterial.HasProperty("_Color")) d.mpb.SetColor("_Color", col);
                d.rend.SetPropertyBlock(d.mpb);
            }
            yield return null;
        }

        if (!visible) SetRenderersEnabled(false);
    }

    private void SetRenderersEnabled(bool en)
    {
        foreach (var r in renderers) if (r) r.enabled = en;
    }

    private void SetCollidersEnabled(bool en)
    {
        foreach (var c in colliders) if (c) c.enabled = en;
    }

    private static IEnumerator Wait(float s)
    {
        float t = 0f; while (t < s) { t += Time.deltaTime; yield return null; }
    }

    // ---------- Utils ----------
    private static Vector3 AbsVec3(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    private static float MaxAxis(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    // ---------- Gizmos ----------
    private void OnDrawGizmos()
    {
        if (!pivot) pivot = transform;

        Gizmos.color = gizmoColor;
        Matrix4x4 m = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
        Gizmos.matrix = m;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                Gizmos.DrawCube(detectPosition, boxSize);
                Gizmos.DrawWireCube(detectPosition, boxSize);
                break;
            case DetectionShape.Sphere:
                Gizmos.DrawSphere(detectPosition, sphereRadius);
                Gizmos.DrawWireSphere(detectPosition, sphereRadius);
                break;
            case DetectionShape.Capsule:
                float half = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = Vector3.up * half;
                Gizmos.DrawWireSphere(detectPosition + up, capsuleRadius);
                Gizmos.DrawWireSphere(detectPosition - up, capsuleRadius);
                Gizmos.DrawLine(detectPosition + up + Vector3.forward * capsuleRadius,  detectPosition - up + Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up - Vector3.forward * capsuleRadius,  detectPosition - up - Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up + Vector3.right * capsuleRadius,   detectPosition - up + Vector3.right * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up - Vector3.right * capsuleRadius,   detectPosition - up - Vector3.right * capsuleRadius);
                break;
            case DetectionShape.Mesh:
                if (meshPreview)
                {
                    Gizmos.DrawMesh(meshPreview, detectPosition, Quaternion.identity, Vector3.one);
                    Gizmos.DrawWireMesh(meshPreview, detectPosition, Quaternion.identity, Vector3.one);
                }
                break;
        }
    }
}
