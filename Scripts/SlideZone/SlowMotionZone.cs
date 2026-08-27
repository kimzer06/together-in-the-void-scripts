using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using Unity.Cinemachine;
using StarterAssets;

/// <summary>
/// Trigger zone: per-player slow motion (SpeedMultiplier) + optional URP Volume + Cinemachine lerp (owner client only).
/// กล้องใช้ ref-count กันโซนซ้อน — ออกครบทุกโซนถึงคืนค่า FOV/Damping
/// </summary>
[RequireComponent(typeof(Collider))]
public class SlowMotionZone : MonoBehaviour
{
    [Header("Slow Motion (Gameplay)")]
    [Tooltip("ตัวคูณความเร็วเมื่ออยู่ใน zone (0.3 = 30% ของปกติ)")]
    [Range(0.05f, 1f)]
    public float slowMultiplier = 0.3f;

    [Tooltip("เวลา lerp เข้า slow-mo (วินาที)")]
    [Range(0f, 2f)]
    public float enterTransitionDuration = 0.3f;

    [Tooltip("เวลา lerp กลับปกติเมื่อออกจาก zone (วินาที)")]
    [Range(0f, 2f)]
    public float exitTransitionDuration = 0.2f;

    [Header("URP Post-processing (optional)")]
    [Tooltip("Local/Global Volume บนโซนนี้ — Weight จะ lerp 0↔1 (เฉพาะ owner ที่เข้า)")]
    public Volume zoneVolume;

    [Tooltip("ค่า Weight สูงสุดตอนอยู่ในโซน")]
    [Range(0f, 1f)]
    public float volumeTargetWeight = 1f;

    [Header("Cinemachine 3.x (optional, owner client)")]
    [Tooltip("ดึง FOV เข้า (ค่าบวก = zoom in)")]
    [Range(0f, 25f)]
    public float fovPullInDegrees = 4f;

    [Tooltip("คูณ Damping ของ CinemachineThirdPersonFollow ตอนอยู่ในโซน (>1 = กล้องตามช้า/หนักขึ้น)")]
    [Range(1f, 4f)]
    public float dampingMultiplierWhenSlow = 1.45f;

    [Header("Audio (owner client)")]
    [Tooltip("เล่นเฉพาะเครื่องของผู้เล่นที่เป็น owner เมื่อเข้าโซน")]
    public AudioClip enterZoneClip;

    [Range(0f, 1f)]
    public float enterVolume = 1f;

    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ) — เหมือน InteractSwitch")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    private Coroutine _volumeLerpCoroutine;

    private void Awake()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.TryGetComponent<NetworkObject>(out var netObj) || !netObj.IsOwner) return;
        if (!other.TryGetComponent<ThirdPersonController_Rigidbody>(out var ctrl)) return;

        ctrl.SetSpeedMultiplierServerRpc(slowMultiplier, enterTransitionDuration);
        SlowMotionVisualCoordinator.OnEnterZone(ctrl, netObj, this);
        PlayEnterSound(other.transform.position);
    }

    private void PlayEnterSound(Vector3 worldPosition)
    {
        if (enterZoneClip == null) return;

        var go = new GameObject("SlowMoZoneOneShotAudio");
        go.transform.position = worldPosition;
        var src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 1f;
        if (outputMixerGroup != null)
            src.outputAudioMixerGroup = outputMixerGroup;
        src.PlayOneShot(enterZoneClip, enterVolume);
        Destroy(go, enterZoneClip.length + 0.1f);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.TryGetComponent<NetworkObject>(out var netObj) || !netObj.IsOwner) return;
        if (!other.TryGetComponent<ThirdPersonController_Rigidbody>(out var ctrl)) return;

        ctrl.SetSpeedMultiplierServerRpc(1f, exitTransitionDuration);
        SlowMotionVisualCoordinator.OnExitZone(ctrl, this);
    }

    internal void LerpVolumeWeight(float from, float to, float duration)
    {
        if (zoneVolume == null) return;
        if (_volumeLerpCoroutine != null) StopCoroutine(_volumeLerpCoroutine);
        if (duration <= 0f)
        {
            zoneVolume.weight = to;
            return;
        }
        _volumeLerpCoroutine = StartCoroutine(CoLerpVolumeWeight(from, to, duration));
    }

    private IEnumerator CoLerpVolumeWeight(float from, float to, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / duration);
            zoneVolume.weight = Mathf.Lerp(from, to, u);
            yield return null;
        }
        zoneVolume.weight = to;
        _volumeLerpCoroutine = null;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.25f);
        var col = GetComponent<Collider>();
        if (col is BoxCollider box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.7f);
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawSphere(sphere.center, sphere.radius);
            Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.7f);
            Gizmos.DrawWireSphere(sphere.center, sphere.radius);
            Gizmos.matrix = Matrix4x4.identity;
        }

        UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 1f);
        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 1.5f,
            $"SlowMo ×{slowMultiplier:F2}");
    }
#endif
}

/// <summary>
/// Ref-count ต่อ ThirdPersonController_Rigidbody — กล้องคืนค่าเมื่อออกจากทุก SlowMotionZone ที่ทับกัน
/// </summary>
internal static class SlowMotionVisualCoordinator
{
    private sealed class CamState
    {
        public int RefCount;
        public float RestFov;
        public float RestOrthoSize;
        public Vector3 RestDamping;
        /// <summary>true = ใช้ FieldOfView, false = ใช้ OrthographicSize</summary>
        public bool UsePerspectiveFov;
        public bool HasFollow;
        public CinemachineCamera Vcam;
        public CinemachineThirdPersonFollow Follow;
        public Coroutine Running;
        public ThirdPersonController_Rigidbody Host;
    }

    private static readonly Dictionary<ThirdPersonController_Rigidbody, CamState> s_camByCtrl = new();

    public static void OnEnterZone(ThirdPersonController_Rigidbody ctrl, NetworkObject playerNetObj, SlowMotionZone zone)
    {
        if (ctrl == null || playerNetObj == null || zone == null) return;

        if (!TryResolveVcam(ctrl, playerNetObj, out var vcam, out var follow))
            return;

        if (!s_camByCtrl.TryGetValue(ctrl, out var st))
        {
            st = new CamState { Host = ctrl };
            s_camByCtrl[ctrl] = st;
        }

        st.Vcam = vcam;
        st.Follow = follow;
        st.HasFollow = follow != null;

        st.RefCount++;
        if (st.RefCount == 1)
        {
            var lens = vcam.Lens;
            st.UsePerspectiveFov = !lens.Orthographic;
            if (st.UsePerspectiveFov)
                st.RestFov = lens.FieldOfView;
            else
                st.RestOrthoSize = lens.OrthographicSize;
            st.RestDamping = follow != null ? follow.Damping : Vector3.zero;

            if (st.Running != null) ctrl.StopCoroutine(st.Running);
            st.Running = ctrl.StartCoroutine(CoLerpCameraToSlow(st, zone));
        }

        if (zone.zoneVolume != null)
            zone.LerpVolumeWeight(zone.zoneVolume.weight, zone.volumeTargetWeight, zone.enterTransitionDuration);
    }

    public static void OnExitZone(ThirdPersonController_Rigidbody ctrl, SlowMotionZone zone)
    {
        if (ctrl == null || zone == null) return;

        if (zone.zoneVolume != null)
            zone.LerpVolumeWeight(zone.zoneVolume.weight, 0f, zone.exitTransitionDuration);

        if (!s_camByCtrl.TryGetValue(ctrl, out var st)) return;

        st.RefCount--;
        if (st.RefCount < 0) st.RefCount = 0;

        if (st.RefCount == 0 && st.Vcam != null)
        {
            if (st.Running != null) ctrl.StopCoroutine(st.Running);
            st.Running = ctrl.StartCoroutine(CoLerpCameraToRest(st, zone));
        }
    }

    private static bool TryResolveVcam(
        ThirdPersonController_Rigidbody ctrl,
        NetworkObject playerNetObj,
        out CinemachineCamera vcam,
        out CinemachineThirdPersonFollow follow)
    {
        vcam = ctrl.GetComponentInChildren<CinemachineCamera>(true);
        follow = null;
        if (vcam == null) return false;

        var vcamOwner = vcam.GetComponentInParent<NetworkObject>();
        if (vcamOwner == null || vcamOwner != playerNetObj)
        {
            vcam = null;
            return false;
        }

        follow = vcam.GetComponent<CinemachineThirdPersonFollow>();
        return true;
    }

    private static IEnumerator CoLerpCameraToSlow(CamState st, SlowMotionZone zone)
    {
        float dur = Mathf.Max(0.0001f, zone.enterTransitionDuration);
        float t = 0f;
        var vcam = st.Vcam;
        if (vcam == null) yield break;

        float startFov = vcam.Lens.FieldOfView;
        float startOrtho = vcam.Lens.OrthographicSize;
        Vector3 startDamp = st.HasFollow ? st.Follow.Damping : st.RestDamping;

        float targetFov = Mathf.Max(1f, st.RestFov - zone.fovPullInDegrees);
        float orthoZoom = Mathf.Clamp(1f - zone.fovPullInDegrees * 0.02f, 0.55f, 1f);
        float targetOrtho = Mathf.Max(0.01f, st.RestOrthoSize * orthoZoom);
        Vector3 targetDamp = st.HasFollow
            ? Vector3.Scale(st.RestDamping, new Vector3(zone.dampingMultiplierWhenSlow, zone.dampingMultiplierWhenSlow, zone.dampingMultiplierWhenSlow))
            : startDamp;

        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);
            if (vcam == null) yield break;
            var lens = vcam.Lens;
            if (st.UsePerspectiveFov)
                lens.FieldOfView = Mathf.Lerp(startFov, targetFov, u);
            else
                lens.OrthographicSize = Mathf.Lerp(startOrtho, targetOrtho, u);
            vcam.Lens = lens;
            if (st.HasFollow && st.Follow != null)
                st.Follow.Damping = Vector3.Lerp(startDamp, targetDamp, u);
            yield return null;
        }

        if (vcam != null)
        {
            var lens = vcam.Lens;
            if (st.UsePerspectiveFov)
                lens.FieldOfView = targetFov;
            else
                lens.OrthographicSize = targetOrtho;
            vcam.Lens = lens;
        }
        if (st.HasFollow && st.Follow != null)
            st.Follow.Damping = targetDamp;

        st.Running = null;
    }

    private static IEnumerator CoLerpCameraToRest(CamState st, SlowMotionZone zone)
    {
        float dur = Mathf.Max(0.0001f, zone.exitTransitionDuration);
        float t = 0f;
        var vcam = st.Vcam;
        if (vcam == null)
        {
            s_camByCtrl.Remove(st.Host);
            yield break;
        }

        float startFov = vcam.Lens.FieldOfView;
        float startOrtho = vcam.Lens.OrthographicSize;
        Vector3 startDamp = st.HasFollow && st.Follow != null ? st.Follow.Damping : st.RestDamping;

        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);
            if (vcam == null) yield break;
            var lens = vcam.Lens;
            if (st.UsePerspectiveFov)
                lens.FieldOfView = Mathf.Lerp(startFov, st.RestFov, u);
            else
                lens.OrthographicSize = Mathf.Lerp(startOrtho, st.RestOrthoSize, u);
            vcam.Lens = lens;
            if (st.HasFollow && st.Follow != null)
                st.Follow.Damping = Vector3.Lerp(startDamp, st.RestDamping, u);
            yield return null;
        }

        if (vcam != null)
        {
            var lens = vcam.Lens;
            if (st.UsePerspectiveFov)
                lens.FieldOfView = st.RestFov;
            else
                lens.OrthographicSize = st.RestOrthoSize;
            vcam.Lens = lens;
        }
        if (st.HasFollow && st.Follow != null)
            st.Follow.Damping = st.RestDamping;

        st.Running = null;
        s_camByCtrl.Remove(st.Host);
    }
}
