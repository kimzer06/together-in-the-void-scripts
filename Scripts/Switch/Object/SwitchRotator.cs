using UnityEngine;
using DG.Tweening;

public class SwitchRotator : MonoBehaviour
{
    [Header("Basic Settings")]
    [SerializeField] private Transform pivot;
    [SerializeField] private float duration = 0.35f;
    [SerializeField] private Ease ease = Ease.OutCubic;

    [Header("One Shot Mode (Tap Interaction)")]
    [SerializeField] private bool useToggle = false;
    [SerializeField] private Vector3 oneShotDelta = new Vector3(0, 90, 0);
    [SerializeField] private bool clampAfterOneShot = true;

    [Header("State Mode (Hold Interaction)")]
    [Tooltip("มุมของคันโยกตอน 'ปล่อย' (Released)")]
    [SerializeField] private float fromX = 0f;
    [Tooltip("มุมของคันโยกตอน 'กดค้าง' (Pressed)")]
    [SerializeField] private float toX = -45f;

    [Header("Press Visual (Optional Offset)")]
    [SerializeField] private bool enablePressVisual = true;
    [SerializeField] private Vector3 pressLocalOffset = new Vector3(0f, -0.015f, 0f);
    [SerializeField] private float pressAnimDuration = 0.12f;
    [SerializeField] private Ease pressAnimEase = Ease.OutQuad;

    private Tween _rotationTween;
    private Tween _pressTween;
    private Tween _resetDelayTween;  // สำหรับจองการรีเซ็ตล่วงหน้า
    private Transform Piv => pivot ? pivot : transform;
    private Quaternion _baseLocalRot;
    private Vector3 _baseLocalPos;
    private bool _toSide = true;

    private void Awake()
    {
        _baseLocalPos = Piv.localPosition;
        _baseLocalRot = Piv.localRotation;

        // ตั้งค่ามุมเริ่มต้นให้เป็นมุมตอน 'ปล่อย' (fromX)
        var startRotation = _baseLocalRot * Quaternion.Euler(fromX, 0f, 0f);
        Piv.localRotation = startRotation;
        _toSide = true;
    }

    /// <summary>
    /// สั่งหมุนหนึ่งครั้ง (สำหรับโหมด Tap)
    /// </summary>
    public void PlayOneShot()
    {
        if (_rotationTween != null && _rotationTween.IsActive()) _rotationTween.Kill(true);

        if (useToggle)
        {
            float targetX = _toSide ? toX : fromX;
            _toSide = !_toSide;
            Quaternion target = _baseLocalRot * Quaternion.Euler(targetX, 0f, 0f);
            _rotationTween = Piv.DOLocalRotateQuaternion(target, duration).SetEase(ease);
        }
        else
        {
            // โหมด OneShot: เพิ่มมุมแบบ Relative
            _rotationTween = Piv.DORotate(oneShotDelta, duration, RotateMode.LocalAxisAdd)
                .SetRelative(true)
                .SetEase(ease)
                .OnComplete(() =>
                {
                    if (!clampAfterOneShot) return;
                    // Clamp logic to prevent value wrapping
                    Quaternion current = Piv.localRotation;
                    Quaternion delta = Quaternion.Inverse(_baseLocalRot) * current;
                    Vector3 deltaEuler = delta.eulerAngles;
                    deltaEuler.x = Mathf.DeltaAngle(0f, deltaEuler.x);
                    deltaEuler.y = Mathf.DeltaAngle(0f, deltaEuler.y);
                    deltaEuler.z = Mathf.DeltaAngle(0f, deltaEuler.z);
                    Piv.localRotation = _baseLocalRot * Quaternion.Euler(deltaEuler);
                });
        }
    }

    /// <summary>
    /// สั่งเปลี่ยนสถานะ กด/ปล่อย (สำหรับโหมด Hold)
    /// </summary>
    public void SetPressed(bool pressed)
    {
        // หยุดอนิเมชั่นเก่าก่อนเริ่มใหม่
        if (_rotationTween != null && _rotationTween.IsActive()) _rotationTween.Kill(true);
        if (_pressTween != null && _pressTween.IsActive()) _pressTween.Kill(true);
        CancelPendingReset();

        // --- 1. อนิเมชั่นการยุบตัว (Position Offset) ---
        if (enablePressVisual)
        {
            var targetPos = pressed ? (_baseLocalPos + pressLocalOffset) : _baseLocalPos;
            _pressTween = Piv.DOLocalMove(targetPos, pressAnimDuration).SetEase(pressAnimEase);
        }

        // --- 2. อนิเมชั่นการโยก (Rotation) ---
        // CHANGE: เพิ่มส่วนนี้เข้ามา
        float targetAngleX = pressed ? toX : fromX;
        Quaternion targetRotation = _baseLocalRot * Quaternion.Euler(targetAngleX, 0f, 0f);

        // ใช้ duration ตัวหลักสำหรับอนิเมชั่นการโยก
        _rotationTween = Piv.DOLocalRotateQuaternion(targetRotation, duration).SetEase(ease);
    }

    /// <summary>
    /// รีเซ็ตกลับสถานะเริ่มต้น (snap ทันที)
    /// </summary>
    public void ResetToBase(bool forToggleToStartAtFromX = true)
    {
        if (_rotationTween != null && _rotationTween.IsActive()) _rotationTween.Kill(true);
        if (_pressTween != null && _pressTween.IsActive()) _pressTween.Kill(true);
        CancelPendingReset();

        Piv.localPosition = _baseLocalPos;
        Piv.localRotation = _baseLocalRot * Quaternion.Euler(fromX, 0f, 0f); // กลับไปที่มุมเริ่มต้น
        _toSide = true;
    }

    /// <summary>
    /// รีเซ็ตกลับสถานะเริ่มต้นแบบ Animated (DOTween) พร้อมหน่วงเวลาได้
    /// ถ้า delay > 0: รอให้ tween ปัจจุบัน (PlayOneShot) เล่นจบก่อน แล้วค่อยหมุนกลับ
    /// </summary>
    public void ResetToBaseAnimated(float delay = 0f)
    {
        CancelPendingReset();

        if (delay <= 0f)
        {
            // รีเซ็ตทันที
            if (_rotationTween != null && _rotationTween.IsActive()) _rotationTween.Kill(false);
            if (_pressTween != null && _pressTween.IsActive()) _pressTween.Kill(false);
            DoResetAnimation();
        }
        else
        {
            // รอ delay ก่อน — ไม่ kill tween ปัจจุบัน ให้ PlayOneShot เล่นสมูทจนจบ
            _resetDelayTween = DOVirtual.DelayedCall(delay, () =>
            {
                if (_rotationTween != null && _rotationTween.IsActive()) _rotationTween.Kill(false);
                if (_pressTween != null && _pressTween.IsActive()) _pressTween.Kill(false);
                DoResetAnimation();
            });
        }
    }

    private void DoResetAnimation()
    {
        Quaternion targetRotation = _baseLocalRot * Quaternion.Euler(fromX, 0f, 0f);
        _rotationTween = Piv.DOLocalRotateQuaternion(targetRotation, duration).SetEase(ease);

        if (enablePressVisual)
        {
            _pressTween = Piv.DOLocalMove(_baseLocalPos, pressAnimDuration).SetEase(pressAnimEase);
        }

        _toSide = true;
    }

    private void CancelPendingReset()
    {
        if (_resetDelayTween != null && _resetDelayTween.IsActive())
        {
            _resetDelayTween.Kill();
            _resetDelayTween = null;
        }
    }
}