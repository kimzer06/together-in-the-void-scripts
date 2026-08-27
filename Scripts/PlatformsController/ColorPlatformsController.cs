using System;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class ColorPlatformsController : NetworkBehaviour
{
    public enum Slot { None = -1, Red = 0, Yellow = 1, Green = 2 }

    [Header("Platforms by Color (ใส่กี่อันก็ได้ต่อสี)")]
    [SerializeField] private MovingPlatform_Triggered[] redPlatforms;
    [SerializeField] private MovingPlatform_Triggered[] yellowPlatforms;
    [SerializeField] private MovingPlatform_Triggered[] greenPlatforms;

    [Header("Initial")]
    [SerializeField] private Slot initialActive = Slot.None;

    public event Action<Slot> OnActiveSlotLocal;

    private NetworkVariable<Slot> activeSlot = new NetworkVariable<Slot>(
        Slot.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public Slot ActiveSlotValue => activeSlot.Value;

    public override void OnNetworkSpawn()
    {
        activeSlot.OnValueChanged += OnActiveChanged;

        if (IsServer)
            SetOnlyServer(initialActive);
        else
            ApplyActive(activeSlot.Value); // sync ภาพฝั่ง client ตอน spawn
    }

    void OnDestroy()
    {
        activeSlot.OnValueChanged -= OnActiveChanged;
    }

    void OnActiveChanged(Slot prev, Slot now)
    {
        ApplyActive(now);
        OnActiveSlotLocal?.Invoke(now);
    }

    void ApplyActive(Slot slot)
    {
        // บน Server เท่านั้นที่สั่ง Extend/Retract (แพลตฟอร์มจะกระจาย ClientRpc เอง)
        if (!IsServer) return;

        // Helper: สั่งกับ array ทั้งชุด
        void SetGroup(MovingPlatform_Triggered[] group, bool extend)
        {
            if (group == null) return;
            for (int i = 0; i < group.Length; i++)
            {
                var p = group[i];
                if (!p) continue;
                if (extend) p.Extend();
                else p.Retract();
            }
        }

        // Active ชุดเดียว ที่เหลือ Retract
        SetGroup(redPlatforms,    slot == Slot.Red);
        SetGroup(yellowPlatforms, slot == Slot.Yellow);
        SetGroup(greenPlatforms,  slot == Slot.Green);
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetActiveSlotServerRpc(Slot slot) => SetOnlyServer(slot);

    private void SetOnlyServer(Slot slot)
    {
        if (!IsServer) return;

        // Toggle: ถ้ากดซ้ำสีเดิม → ปิดหมด (None)
        if (slot == activeSlot.Value) slot = Slot.None;

        activeSlot.Value = slot;  // Triggers OnValueChanged → ApplyActive ทุกเครื่อง
        // หมายเหตุ: activeSlot.Value == None → ApplyActive จะ Retract ทุกสี
        if (slot == Slot.None)
        {
            // Retract ทั้งหมด
            RetractAll();
        }
    }

    private void RetractAll()
    {
        void RetractGroup(MovingPlatform_Triggered[] group)
        {
            if (group == null) return;
            for (int i = 0; i < group.Length; i++)
            {
                var p = group[i];
                if (!p) continue;
                p.Retract();
            }
        }

        RetractGroup(redPlatforms);
        RetractGroup(yellowPlatforms);
        RetractGroup(greenPlatforms);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // ป้องกัน null array เพื่อคุณจะลากวางใน Inspector ได้สะดวก
        if (redPlatforms    == null) redPlatforms    = new MovingPlatform_Triggered[0];
        if (yellowPlatforms == null) yellowPlatforms = new MovingPlatform_Triggered[0];
        if (greenPlatforms  == null) greenPlatforms  = new MovingPlatform_Triggered[0];
    }
#endif
}