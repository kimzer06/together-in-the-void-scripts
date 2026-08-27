using UnityEngine;
using UnityEngine.InputSystem;

public class CharacterPreviewRotator : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private Camera cameraForOrbit;

    [Header("Rotate Settings")]
    [SerializeField] private float rotateSpeed = 0.25f;
    [SerializeField] private float inertia = 8f;

    [Header("Pitch (Up/Down)")]
    [SerializeField] private bool enablePitch = false;
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 20f;

    private Vector2 currentVel;
    private Vector2 currentAngles;

    // static เก็บว่าเรากำลังลากตัวไหนอยู่
    private static CharacterPreviewRotator activeTarget;

    void Awake()
    {
        if (!target) target = transform;
        if (!cameraForOrbit) cameraForOrbit = Camera.main;
    }

    void Update()
    {
        if (Mouse.current == null) return;

        // เริ่มกด -> ยิง Raycast หา target
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            Ray ray = cameraForOrbit.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                CharacterPreviewRotator rot = hit.collider.GetComponent<CharacterPreviewRotator>();
                if (rot != null)
                    activeTarget = rot;
            }
        }

        // ปล่อยปุ่ม -> ยกเลิก target
        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            if (activeTarget == this) activeTarget = null;
        }

        // หมุนเฉพาะตัวที่ active อยู่
        if (activeTarget != this) return;

        bool isDragging = Mouse.current.leftButton.isPressed;
        Vector2 delta = isDragging ? Mouse.current.delta.ReadValue() : Vector2.zero;

        Vector2 targetVel = delta * rotateSpeed;
        if (!isDragging)
            targetVel = Vector2.Lerp(currentVel, Vector2.zero, Time.deltaTime * inertia);

        currentVel = targetVel;

        if (enablePitch)
        {
            currentAngles.x = Mathf.Clamp(currentAngles.x - currentVel.y * Time.deltaTime, minPitch, maxPitch);
            currentAngles.y -= currentVel.x * Time.deltaTime;
            target.rotation = Quaternion.Euler(currentAngles.x, currentAngles.y, 0f);
        }
        else
        {
            float yaw = -currentVel.x * Time.deltaTime;
            target.Rotate(Vector3.up, yaw, Space.World);
        }
    }
}   