using UnityEngine;
using DG.Tweening;

public class PlatformRotating : MonoBehaviour
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    [Header("Rotation")]
    public Axis rotationAxis = Axis.Z;
    public Direction rotationDirection = Direction.Positive; // เลือกทิศทาง
    public float rotationAngle = 360f;
    public float duration = 2f;
    public RotateMode rotateMode = RotateMode.FastBeyond360;

    private Tween _tween;

    void Start()
    {
        // กำหนดทิศทาง ถ้า Negative → หมุนมุมติดลบ
        float angle = (rotationDirection == Direction.Positive) ? rotationAngle : -rotationAngle;

        Vector3 targetRot = rotationAxis switch
        {
            Axis.X => new Vector3(angle, 0, 0),
            Axis.Y => new Vector3(0, angle, 0),
             _ => new Vector3(0, 0, angle),
        };

        _tween = transform
            .DOLocalRotate(targetRot, duration, rotateMode)
            .SetEase(Ease.Linear)
            .SetLoops(-1, LoopType.Incremental)
            .SetId(gameObject)     // ทำให้ Freezable จับได้
            .SetTarget(transform); // ทำให้ Pause(transform) จับได้
    }

    void OnDisable()
    {
        if (_tween != null && _tween.IsActive()) _tween.Kill();
    }

    void OnDestroy()
    {
        if (_tween != null && _tween.IsActive()) _tween.Kill();
    }
}