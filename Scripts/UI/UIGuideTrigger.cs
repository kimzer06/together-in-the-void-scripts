using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using DG.Tweening;
public class UIGuideTrigger : NetworkBehaviour
{
    private enum DetectionShape { Box, Sphere, Capsule }

    [Header("UI Panel ใน Scene (เห็นเฉพาะ Local)")]
    [SerializeField] private GameObject uiPanelInScene;

    [Header("Timing")]
    [SerializeField] private float fadeInSeconds = 0.5f;
    [SerializeField] private float holdSeconds = 2.5f;
    [SerializeField] private float fadeOutSeconds = 0.5f;

    [Header("Detect Area")]
    [SerializeField] private Transform pivot;                  
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    [SerializeField] private Vector3 boxSize = new Vector3(2, 2, 2);
    [SerializeField] private float sphereRadius = 1.0f;
    [SerializeField] private float capsuleRadius = 0.75f;
    [SerializeField] private float capsuleHeight = 2.0f;

    [Header("Filter")]
    [SerializeField] private LayerMask detectLayers = ~0;
    [SerializeField] private string requiredTag = "Player";     

    [Header("Behavior")]
    [SerializeField] private bool oneShot = true;
    [SerializeField] private float detectCooldown = 0.2f;

    [Header("Gizmos Settings")]
    [SerializeField] private Color gizmoFillColor = new Color(0f, 1f, 0f, 0.15f);
    [SerializeField] private Color gizmoLineColor = new Color(0f, 1f, 0f, 0.8f);

    private CanvasGroup _cg;
    private bool _isAnimating;
    private bool _wasInside;        
    private bool _consumed;         
    private float _lastDetectTime = -999f;

    private void Awake()
    {
        if (!pivot) pivot = transform;

        if (uiPanelInScene != null)
        {
            uiPanelInScene.SetActive(false);
            _cg = uiPanelInScene.GetComponent<CanvasGroup>();
            if (_cg == null) _cg = uiPanelInScene.AddComponent<CanvasGroup>();
            _cg.alpha = 0f;
        }
    }

    private void Update()
    {
        if (_consumed && oneShot) return;

        bool insideNow = IsLocalPlayerInside();

        if (!_wasInside && insideNow && Time.time - _lastDetectTime > detectCooldown)
        {
            _lastDetectTime = Time.time;
            ShowUIOnce();
            if (oneShot) _consumed = true;
        }

        _wasInside = insideNow;
    }

    private bool IsLocalPlayerInside()
    {
        Vector3 worldPos = transform.TransformPoint(detectPosition);
        Quaternion worldRot = pivot ? pivot.rotation : transform.rotation;

        Collider[] hits = null;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                hits = Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers);
                break;
            case DetectionShape.Sphere:
                hits = Physics.OverlapSphere(worldPos, sphereRadius, detectLayers);
                break;
            case DetectionShape.Capsule:
                float halfHeight = Mathf.Max(0f, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 a = worldPos + (pivot ? pivot.up : Vector3.up) * halfHeight;
                Vector3 b = worldPos - (pivot ? pivot.up : Vector3.up) * halfHeight;
                hits = Physics.OverlapCapsule(a, b, capsuleRadius, detectLayers);
                break;
        }

        if (hits == null || hits.Length == 0) return false;

        foreach (var c in hits)
        {
            if (!string.IsNullOrEmpty(requiredTag) && !c.CompareTag(requiredTag)) continue;
            if (!c.TryGetComponent<NetworkObject>(out var no)) continue;
            if (no.IsLocalPlayer) return true;
        }

        return false;
    }

    private void ShowUIOnce()
    {
        if (uiPanelInScene == null) return;
        if (_isAnimating) return;

        uiPanelInScene.SetActive(true);
        _cg.DOKill();
        _cg.alpha = 0f;

        _isAnimating = true;

        Sequence seq = DOTween.Sequence();
        seq.Append(_cg.DOFade(1f, fadeInSeconds).SetEase(Ease.OutQuad));
        seq.AppendInterval(holdSeconds);
        seq.Append(_cg.DOFade(0f, fadeOutSeconds).SetEase(Ease.InQuad));
        seq.OnComplete(() =>
        {
            uiPanelInScene.SetActive(false);
            _isAnimating = false;

            if (oneShot) enabled = false;
        });
    }

    private void OnDrawGizmos()
    {
        if (!pivot) pivot = transform;

        Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);

        // วาด Fill
        Gizmos.color = gizmoFillColor;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                Gizmos.DrawCube(detectPosition, boxSize);
                break;
            case DetectionShape.Sphere:
                Gizmos.DrawSphere(detectPosition, sphereRadius);
                break;
            case DetectionShape.Capsule:
                float hh = Mathf.Max(0f, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = Vector3.up * hh;
                Gizmos.DrawSphere(detectPosition + up, capsuleRadius);
                Gizmos.DrawSphere(detectPosition - up, capsuleRadius);
                break;
        }

        // วาดเส้นรอบนอก
        Gizmos.color = gizmoLineColor;
        switch (detectionShape)
        {
            case DetectionShape.Box:
                Gizmos.DrawWireCube(detectPosition, boxSize);
                break;
            case DetectionShape.Sphere:
                Gizmos.DrawWireSphere(detectPosition, sphereRadius);
                break;
            case DetectionShape.Capsule:
                float hh = Mathf.Max(0f, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = Vector3.up * hh;
                Gizmos.DrawWireSphere(detectPosition + up, capsuleRadius);
                Gizmos.DrawWireSphere(detectPosition - up, capsuleRadius);
                break;
        }
    }
}


