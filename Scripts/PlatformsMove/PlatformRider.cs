using UnityEngine;
using Unity.Netcode;

[RequireComponent(typeof(Rigidbody))]
public class PlatformRider : NetworkBehaviour
{
    [Header("Options")]
    public bool onlyForOwner = true;   // ใช้ client-authoritative player → true
    public bool applyRotation = true;  // ให้ตามการหมุนแพลตฟอร์มด้วย

    Rigidbody rb;

    Transform currentPlatform;
    Matrix4x4 lastPlatMatrix;
    Matrix4x4 lastPlatMatrixInv;
    bool hasLastMatrix;

    // === Network sync: บอก Non-Owner ว่า Owner กำลังยืนบน Platform ไหม ===
    private readonly NetworkVariable<bool> _isOnPlatform = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Non-Owner ใช้อ่านว่า Owner กำลังอยู่บน Platform หรือไม่
    /// </summary>
    public NetworkVariable<bool> IsOnPlatform => _isOnPlatform;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void OnTriggerEnter(Collider other)
    {
        if (!IsOwner) return;

        var zone = other.GetComponent<PlatformRideZone>();
        if (!zone) return;

        currentPlatform = zone.platformRoot ? zone.platformRoot : zone.transform;
        lastPlatMatrix = currentPlatform.localToWorldMatrix;
        lastPlatMatrixInv = lastPlatMatrix.inverse;
        hasLastMatrix = true;

        _isOnPlatform.Value = true;
    }

    void OnTriggerExit(Collider other)
    {
        if (!IsOwner) return;

        var zone = other.GetComponent<PlatformRideZone>();
        if (!zone) return;

        if (currentPlatform == (zone.platformRoot ? zone.platformRoot : zone.transform))
        {
            currentPlatform = null;
            hasLastMatrix = false;

            _isOnPlatform.Value = false;
        }
    }

    void FixedUpdate()
    {
        if (onlyForOwner && !IsOwner) return;
        if (!currentPlatform || !hasLastMatrix) return;

        var curr = currentPlatform.localToWorldMatrix;

        Vector3 p0 = rb.position;
        Vector3 local = lastPlatMatrixInv.MultiplyPoint3x4(p0);
        Vector3 p1 = curr.MultiplyPoint3x4(local);
        Vector3 dp = p1 - p0;
        rb.MovePosition(p0 + dp);

        if (applyRotation)
        {
            Quaternion lastRot = Quaternion.LookRotation(
                lastPlatMatrix.GetColumn(2), lastPlatMatrix.GetColumn(1));
            Quaternion currRot = Quaternion.LookRotation(
                curr.GetColumn(2), curr.GetColumn(1));
            Quaternion dRot = currRot * Quaternion.Inverse(lastRot);
            rb.MoveRotation(dRot * rb.rotation);
        }

        lastPlatMatrix = curr;
        lastPlatMatrixInv = curr.inverse;
    }
}
