using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Events;

/// <summary>
/// จัดการ Puzzle เสา - ตรวจสอบลำดับการชนและแสดงผลสำเร็จ/ล้มเหลว
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class PillarPuzzleManager : NetworkBehaviour
{
    [Header("Pillar References")]
    [Tooltip("รายการเสาทั้งหมดใน puzzle นี้")]
    [SerializeField] private List<PuzzlePillar> pillars = new();

    [Header("Correct Order")]
    [Tooltip("ลำดับที่ถูกต้องของ PillarOrder ที่ต้องชน (เช่น 1, 2, 3)")]
    [SerializeField] private int[] correctOrder = { 1, 2, 3 };

    [Header("Timing")]
    [Tooltip("ระยะเวลาเรืองแสงแดงเมื่อล้มเหลว (วินาที)")]
    [SerializeField, Min(0.1f)] private float failGlowDuration = 1f;

    [Tooltip("ดีเลย์ก่อนเปลี่ยนสีหลังจากชนครบ (วินาที)")]
    [SerializeField, Min(0f)] private float resultDelay = 0.3f;

    [Header("Events")]
    [Tooltip("เรียกเมื่อ puzzle สำเร็จ (ชนลำดับถูกต้อง)")]
    public UnityEvent OnPuzzleCompleted;

    [Tooltip("เรียกเมื่อ puzzle ล้มเหลว (ชนลำดับผิด)")]
    public UnityEvent OnPuzzleFailed;

    [Tooltip("เรียกเมื่อ puzzle ถูก reset")]
    public UnityEvent OnPuzzleReset;

    // --- Runtime State ---
    private readonly List<int> _hitSequence = new();
    private bool _isCompleted = false;
    private bool _isProcessing = false;

    public bool IsCompleted => _isCompleted;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            ResetPuzzleState();
        }
    }

    /// <summary>
    /// เรียกจาก PuzzlePillar เมื่อเสาถูกชน (Server-side)
    /// </summary>
    public void Server_OnPillarHit(PuzzlePillar pillar)
    {
        if (!IsServer) return;
        if (_isCompleted) return;
        if (_isProcessing) return;

        // บันทึกลำดับที่ถูกชน
        _hitSequence.Add(pillar.PillarOrder);

        Debug.Log($"[PillarPuzzleManager] เสา {pillar.PillarOrder} ถูกชน, ลำดับปัจจุบัน: [{string.Join(", ", _hitSequence)}]");

        // ตรวจสอบว่าชนครบตามจำนวนที่กำหนดหรือยัง
        if (_hitSequence.Count >= correctOrder.Length)
        {
            _isProcessing = true;
            StartCoroutine(ValidateAndShowResult());
        }
    }

    private IEnumerator ValidateAndShowResult()
    {
        // รอสักครู่ก่อนแสดงผล
        if (resultDelay > 0f)
            yield return new WaitForSeconds(resultDelay);

        bool isCorrect = ValidateSequence();

        if (isCorrect)
        {
            OnSuccess();
        }
        else
        {
            OnFail();
        }

        _isProcessing = false;
    }

    /// <summary>
    /// ตรวจสอบว่าลำดับที่ชนตรงกับลำดับที่ถูกต้องหรือไม่
    /// </summary>
    private bool ValidateSequence()
    {
        if (_hitSequence.Count != correctOrder.Length)
            return false;

        for (int i = 0; i < correctOrder.Length; i++)
        {
            if (_hitSequence[i] != correctOrder[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// เรียกเมื่อลำดับถูกต้อง - เรืองเขียวค้างถาวร
    /// </summary>
    private void OnSuccess()
    {
        Debug.Log("[PillarPuzzleManager] ✓ สำเร็จ! ลำดับถูกต้อง");

        _isCompleted = true;

        // เรืองเขียวค้าง + ประตูยังคงปิดตลอดกาล
        foreach (var pillar in pillars)
        {
            if (pillar != null)
                pillar.SetSuccessGlow();
            // ไม่เปิดประตู: ปิดค้างตลอดกาล
        }

        // UnityEvent รันเฉพาะเครื่องที่ Invoke — ส่งไปทุก client (รวม Host) ผ่าน ClientRpc
        NotifyPuzzleCompletedClientRpc();
    }

    /// <summary>
    /// เรียกเมื่อลำดับผิด - เรืองแดง 1 วินาที แล้วรีเซ็ต
    /// </summary>
    private void OnFail()
    {
        Debug.Log("[PillarPuzzleManager] ✗ ล้มเหลว! ลำดับผิด");

        // เรืองแดงทุกเสา + ทำลายหินที่เก็บไว้
        foreach (var pillar in pillars)
        {
            if (pillar != null)
            {
                pillar.SetFailGlow();
                pillar.DestroyStoredBoulder();
            }
        }

        NotifyPuzzleFailedClientRpc();

        // รอแล้วรีเซ็ต (ResetPuzzle จะดับ emission + เปิดประตูกลับ)
        StartCoroutine(ResetAfterDelay(failGlowDuration));
    }

    private IEnumerator ResetAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        ResetPuzzle();
    }

    /// <summary>
    /// รีเซ็ต puzzle เพื่อเล่นใหม่
    /// </summary>
    public void ResetPuzzle()
    {
        if (!IsServer) return;

        ResetPuzzleState();

        // รีเซ็ตเสาทั้งหมด
        foreach (var pillar in pillars)
        {
            if (pillar != null)
                pillar.ResetPillar();
        }

        NotifyPuzzleResetClientRpc();

        Debug.Log("[PillarPuzzleManager] Puzzle ถูกรีเซ็ตแล้ว พร้อมเล่นใหม่");
    }

    [ClientRpc]
    private void NotifyPuzzleCompletedClientRpc()
    {
        OnPuzzleCompleted?.Invoke();
    }

    [ClientRpc]
    private void NotifyPuzzleFailedClientRpc()
    {
        OnPuzzleFailed?.Invoke();
    }

    [ClientRpc]
    private void NotifyPuzzleResetClientRpc()
    {
        OnPuzzleReset?.Invoke();
    }

    private void ResetPuzzleState()
    {
        _hitSequence.Clear();
        _isCompleted = false;
        _isProcessing = false;
    }

    /// <summary>
    /// รีเซ็ต puzzle ผ่าน ServerRpc (สำหรับเรียกจาก Client)
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void ResetPuzzleServerRpc()
    {
        ResetPuzzle();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (correctOrder == null || correctOrder.Length == 0)
            correctOrder = new int[] { 1, 2, 3 };
    }

    private void OnDrawGizmosSelected()
    {
        if (pillars == null || pillars.Count == 0) return;

        Gizmos.color = Color.yellow;
        for (int i = 0; i < pillars.Count; i++)
        {
            if (pillars[i] == null) continue;

            Vector3 pos = pillars[i].transform.position + Vector3.up * 2f;
            UnityEditor.Handles.Label(pos, $"Pillar {pillars[i].PillarOrder}");

            // วาดเส้นเชื่อมตามลำดับที่ถูกต้อง
            if (i > 0 && pillars[i - 1] != null)
            {
                Gizmos.DrawLine(
                    pillars[i - 1].transform.position + Vector3.up,
                    pillars[i].transform.position + Vector3.up
                );
            }
        }
    }
#endif
}
