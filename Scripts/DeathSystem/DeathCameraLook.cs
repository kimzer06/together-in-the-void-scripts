using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// กล้องช่วงตาย: โคจรรอบจุดโฟกัส + ฟีลอินพุตเหมือนตอนเล่นจริง
public class DeathCameraLook : MonoBehaviour
{
    [Header("Orbit")]
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float maxDistance = 6.0f;
    [SerializeField] private float focusHeight = 1.2f;
    [SerializeField] private float collisionRadius = 0.15f;
    [SerializeField] private LayerMask collisionMask = ~0;

    [Header("Fallback Look (ใช้เมื่อไม่ได้ผูก Action)")]
    [SerializeField] private float sensitivityX = 1.0f;
    [SerializeField] private float sensitivityY = 1.0f;
    [SerializeField] private bool invertY = false;

    // clamp จากคอนโทรลเลอร์ (ค่า fallback หากไม่คอนฟิก)
    private float _bottomClamp = -30f;
    private float _topClamp = 70f;

    // สถานะ
    private Vector3 _focusPoint;
    private float _yaw, _pitch, _distance;

#if ENABLE_INPUT_SYSTEM
    private InputAction _lookAction; // ใช้ Action "Look" เดียวกับเกมเพลย์ (ถ้ามี)
#endif

    public void ConfigureFromTPC(StarterAssets.ThirdPersonController_Rigidbody tpc)
    {
        if (tpc == null) return;
        _bottomClamp = tpc.BottomClamp;
        _topClamp = tpc.TopClamp;
    }

#if ENABLE_INPUT_SYSTEM
    public void UseLookActionFrom(GameObject playerGO, string actionName = "Look")
    {
        var pi = playerGO ? playerGO.GetComponent<UnityEngine.InputSystem.PlayerInput>() : null;
        _lookAction = null;
        if (pi != null && pi.actions != null)
        {
            _lookAction = pi.actions.FindAction(actionName, throwIfNotFound: false);
            if (_lookAction != null && !_lookAction.enabled) _lookAction.Enable();
        }
    }
#endif

    // ตั้งโฟกัส + มุมเริ่ม + ระยะเริ่ม (ใช้ระยะจากเฟรมสุดท้าย)
    public void SetFocusOrbit(Vector3 focus, float startYaw, float startPitch, float startDistance)
    {
        _focusPoint = focus + Vector3.up * focusHeight;
        _yaw = startYaw;
        _pitch = startPitch;
        _distance = Mathf.Clamp(startDistance, minDistance, maxDistance);
        ApplyOrbit();
    }

    private void Update()
    {
        Vector2 look = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        if (_lookAction != null)
        {
            // ฟีลเดียวกับคอนโทรลเลอร์หลัก: เมาส์ไม่คูณ dt / โปรเซสเซอร์เดียวกัน
            look = _lookAction.ReadValue<Vector2>();
        }
        else
#endif
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null) look = Mouse.current.delta.ReadValue();
#else
            look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
#endif
            float ySign = invertY ? -1f : 1f;
            look = new Vector2(look.x * sensitivityX, look.y * sensitivityY * ySign);
        }

        // เมาส์ไม่คูณ dt เหมือน HandleCameraRotation() ของคอนโทรลเลอร์คุณ
        _yaw += look.x;
        _pitch += look.y;

        _yaw = ClampAngle(_yaw, float.MinValue, float.MaxValue);
        _pitch = ClampAngle(_pitch, _bottomClamp, _topClamp);

        ApplyOrbit();
    }

    private void ApplyOrbit()
    {
        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 desiredPos = _focusPoint + rot * (Vector3.back * _distance);

        // กันกล้องทะลุฉาก
        Vector3 dir = desiredPos - _focusPoint;
        float dist = dir.magnitude;
        if (dist > 0.001f)
        {
            dir /= dist;
            if (Physics.SphereCast(_focusPoint, collisionRadius, dir, out RaycastHit hit, dist, collisionMask, QueryTriggerInteraction.Ignore))
            {
                desiredPos = hit.point - dir * 0.05f;
            }
        }

        transform.SetPositionAndRotation(desiredPos, rot);
    }

    private static float ClampAngle(float angle, float min, float max)
    {
        if (angle < -360f) angle += 360f;
        if (angle > 360f) angle -= 360f;
        return Mathf.Clamp(angle, min, max);
    }
}