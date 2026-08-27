using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// ซิงค์ตำแหน่งของ Player แบบ Extrapolation เฉพาะตอนอยู่บน Moving Platform
/// ปกติใช้ ClientNetworkTransform — เปลี่ยนเป็น Extrapolation เฉพาะเมื่อ PlatformRider.IsOnPlatform = true
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PlayerPositionExtrapolation : NetworkBehaviour
{
    [Header("Send Rate")]
    [SerializeField] float sendRate = 30f;

    [Header("Extrapolation (ใช้เฉพาะตอนอยู่บน Platform)")]
    [SerializeField] float correctionSpeed = 30f;
    [SerializeField] float rotationCorrectionSpeed = 15f;
    [SerializeField] float snapDistance = 5f;
    [SerializeField] float extrapolationBias = 0.03f;
    [SerializeField] float maxExtrapolationTime = 0.15f;

    Rigidbody _rb;
    NetworkTransform _networkTransform;
    NetworkRigidbody _networkRigidbody;
    PlatformRider _platformRider;

    // สถานะ: กำลังใช้ Extrapolation อยู่ไหม
    bool _extrapolationActive;

    // Non-Owner: ข้อมูลจาก Owner
    Vector3 _networkPosition;
    Vector3 _networkVelocity;
    float _networkRotationY;
    float _clientSendServerTime;
    bool _hasReceivedData;

    // Owner: คำนวณ velocity แบบ manual
    Vector3 _lastOwnerPosition;
    bool _hasLastPosition;
    float _sendTimer;
    float _sendInterval;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _rb = GetComponent<Rigidbody>();
        _sendInterval = 1f / Mathf.Max(1f, sendRate);
        _platformRider = GetComponent<PlatformRider>();

        if (IsOwner)
        {
            _lastOwnerPosition = _rb.position;
            _hasLastPosition = true;
        }
        else
        {
            // เก็บ reference ไว้ แต่ยังไม่ปิด — ปิดเฉพาะตอนขึ้น Platform
            _networkTransform = GetComponent<NetworkTransform>();
            _networkRigidbody = GetComponent<NetworkRigidbody>();
            _networkPosition = transform.position;

            // ฟังการเปลี่ยน Platform state จาก Owner
            if (_platformRider)
            {
                _platformRider.IsOnPlatform.OnValueChanged += OnPlatformStateChanged;

                // กรณี spawn มาตอน Owner อยู่บน Platform อยู่แล้ว
                if (_platformRider.IsOnPlatform.Value)
                    EnableExtrapolation();
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner && _platformRider)
        {
            _platformRider.IsOnPlatform.OnValueChanged -= OnPlatformStateChanged;
            if (_extrapolationActive) DisableExtrapolation();
        }
        base.OnNetworkDespawn();
    }

    // === สลับระหว่าง ClientNetworkTransform ↔ Extrapolation ===

    void OnPlatformStateChanged(bool wasOnPlatform, bool isOnPlatform)
    {
        if (IsOwner) return;

        if (isOnPlatform)
            EnableExtrapolation();
        else
            DisableExtrapolation();
    }

    void EnableExtrapolation()
    {
        if (_extrapolationActive) return;
        _extrapolationActive = true;

        // ปิด ClientNetworkTransform + NetworkRigidbody → เราจัดการ sync เอง
        if (_networkTransform) _networkTransform.enabled = false;
        if (_networkRigidbody) _networkRigidbody.enabled = false;
        _rb.isKinematic = true;

        // ตั้งค่าเริ่มต้นให้ extrapolation
        _networkPosition = transform.position;
        _hasReceivedData = false;

        Debug.Log("[Extrapolation] ▶ เปิด Extrapolation (ขึ้น Platform)");
    }

    void DisableExtrapolation()
    {
        if (!_extrapolationActive) return;
        _extrapolationActive = false;

        // เปิด ClientNetworkTransform + NetworkRigidbody กลับมา
        if (_networkRigidbody) _networkRigidbody.enabled = true;
        if (_networkTransform) _networkTransform.enabled = true;

        Debug.Log("[Extrapolation] ⏹ ปิด Extrapolation (ลง Platform)");
    }

    // === Owner: ส่ง position + velocity ===

    void FixedUpdate()
    {
        if (!IsOwner || !IsSpawned) return;

        // คำนวณ velocity จาก position delta (รวม platform movement)
        Vector3 currentPos = _rb.position;
        Vector3 calculatedVelocity;

        if (_hasLastPosition)
            calculatedVelocity = (currentPos - _lastOwnerPosition) / Time.fixedDeltaTime;
        else
            calculatedVelocity = _rb.linearVelocity;

        _lastOwnerPosition = currentPos;
        _hasLastPosition = true;

        // ส่งเฉพาะเมื่ออยู่บน Platform (ประหยัด bandwidth)
        if (_platformRider && !_platformRider.IsOnPlatform.Value) return;

        _sendTimer += Time.fixedDeltaTime;
        if (_sendTimer >= _sendInterval)
        {
            float clientServerTime = (float)NetworkManager.Singleton.ServerTime.Time;
            SendPositionServerRpc(currentPos, calculatedVelocity, transform.eulerAngles.y, clientServerTime);
            _sendTimer = 0f;
        }
    }

    [ServerRpc]
    void SendPositionServerRpc(Vector3 position, Vector3 velocity, float rotationY, float clientServerTime)
    {
        RelayPositionClientRpc(position, velocity, rotationY, clientServerTime);
    }

    [ClientRpc]
    void RelayPositionClientRpc(Vector3 position, Vector3 velocity, float rotationY, float clientServerTime)
    {
        if (IsOwner) return;

        _networkPosition = position;
        _networkVelocity = velocity;
        _networkRotationY = rotationY;
        _clientSendServerTime = clientServerTime;
        _hasReceivedData = true;
    }

    // === Non-Owner: Extrapolation (ทำงานเฉพาะตอนอยู่บน Platform) ===

    void Update()
    {
        if (IsOwner || !IsSpawned || !_extrapolationActive || !_hasReceivedData) return;

        // Extrapolation
        float currentServerTime = (float)NetworkManager.Singleton.ServerTime.Time;
        float elapsed = Mathf.Min(
            Mathf.Max(0f, currentServerTime - _clientSendServerTime + extrapolationBias),
            maxExtrapolationTime);
        Vector3 estimatedPosition = _networkPosition + _networkVelocity * elapsed;

        // Position: Exponential Smoothing
        float distance = Vector3.Distance(transform.position, estimatedPosition);
        if (distance > snapDistance)
        {
            transform.position = estimatedPosition;
        }
        else
        {
            float t = 1f - Mathf.Exp(-correctionSpeed * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, estimatedPosition, t);
        }

        // Rotation
        float rt = 1f - Mathf.Exp(-rotationCorrectionSpeed * Time.deltaTime);
        Vector3 euler = transform.eulerAngles;
        euler.y = Mathf.LerpAngle(euler.y, _networkRotationY, rt);
        transform.eulerAngles = euler;
    }
}
