using Fusion;
using UnityEngine;

/// <summary>
/// object ที่เครื่องอื่นสปอน (proxy) ถูกสร้างที่ตำแหน่งตั้งต้นของ prefab ก่อน แล้ว NetworkTransform ค่อยย้ายไปที่จริงตอน Render
/// แต่โปรเจกต์ปิด Physics.autoSyncTransforms → collider ยังค้างที่จุดเดิม (0,0,0) จนกว่าฟิสิกส์จะเดินรอบถัดไป
/// ระหว่างนั้น raycast/spherecast ชนของที่ไม่มีอยู่จริง — เจอแล้ว: รถไฟเกิดใหม่ทุกคันทำกล้องหดเข้าหาตัวละคร 1 เฟรม
/// (prefab รถไฟวางที่ 0,0,0 ซึ่งพาดผ่านจุดเกิดของ level 02 พอดี) — เครื่องที่สปอนเองก็โดน (collider ค้างที่ 0,0,0 เฟรมแรก)
/// </summary>
public static class FusionProxySpawn
{
    /// <summary>
    /// เรียกใน Spawned() ของ object ที่มี NetworkTransform (หลังโค้ดที่ย้ายตำแหน่งตอนเกิด)
    /// - proxy: วางตาม state ล่าสุดทันที แล้ว sync collider
    /// - เจ้าของ: Runner.Spawn ก็สร้างที่ตำแหน่ง prefab ก่อนแล้วค่อยย้ายเหมือนกัน → sync collider อย่างเดียว
    /// </summary>
    public static void SnapToNetworkState(NetworkBehaviour owner)
    {
        if (owner == null || owner.Object == null || !owner.Object.IsValid) return;
        if (owner.HasStateAuthority)
        {
            Physics.SyncTransforms();
            return;
        }

        var nt = owner.GetComponent<NetworkTransform>();
        if (nt == null) return;

        var data = nt.Data;
        // ติด parent อยู่ = ตำแหน่งเป็น local — ปล่อยให้ NetworkTransform จัดการเอง
        if (data.Parent.IsValid) return;

        owner.transform.SetPositionAndRotation(data.Position, data.Rotation);
        if (owner.TryGetComponent<Rigidbody>(out var rb))
        {
            rb.position = data.Position;
            rb.rotation = data.Rotation;
        }
        Physics.SyncTransforms();
    }
}
