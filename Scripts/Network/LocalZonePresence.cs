using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "ตัวละครของเครื่องนี้" อยู่ในโซน trigger ไหม — ให้เจ้าของรายงานตัวเองไปหา master
/// แทนการให้ master นับจากตัวเพื่อนบนจอตัวเอง
///
/// Shared Mode: ตัวละครเป็นของเจ้าของ ตำแหน่งบนเครื่องเจ้าของจึงตรงที่สุด
/// ส่วนตัวเพื่อนบนจอ master ตามหลังของจริง (latency + buffer ของ interpolation) → นับช้าและเพี้ยนตอนยืนขอบโซน
///
/// ใช้: ส่ง OnTriggerEnter/Exit ของโซนเข้ามา แล้วเรียก <see cref="Evaluate"/> ทุก FixedUpdate
/// — คืน true เมื่อ <see cref="Inside"/> เปลี่ยน (ถึงเวลารายงาน)
/// </summary>
public sealed class LocalZonePresence
{
    private readonly Collider _zone;
    private int _colliders;          // collider ของตัวเราที่ซ้อนโซนอยู่ (ตัวละครอาจมีหลายอัน)
    private PlayerDeath _death;

    public bool Inside { get; private set; }

    public LocalZonePresence(Collider zone) => _zone = zone;

    public void OnTriggerEnter(Collider other)
    {
        if (!TryGetLocalPlayer(other, out var death)) return;
        _colliders++;
        _death = death;
    }

    public void OnTriggerExit(Collider other)
    {
        if (!TryGetLocalPlayer(other, out _)) return;
        if (_colliders > 0) _colliders--;
    }

    /// <summary>คำนวณใหม่ — คืน true ถ้า <see cref="Inside"/> เปลี่ยน</summary>
    public bool Evaluate()
    {
        // ตาย / ถูกวาร์ปออกขณะ collider ปิด → OnTriggerExit ไม่มา — ล้างตัวนับเองเมื่อไม่ซ้อนโซนแล้วจริง
        // (ไม่ล้างตอนแค่ "ซ่อนอยู่" — ตอน respawn ในโซน collider กลับมาก่อนตัวละครโผล่ enter จะหาย)
        if (_colliders > 0 && (_death == null || !StillOverlaps(_zone, _death))) _colliders = 0;

        bool inside = _colliders > 0 && !_death.IsHiddenState;
        if (inside == Inside) return false;
        Inside = inside;
        return true;
    }

    public void Reset()
    {
        _colliders = 0;
        _death = null;
        Inside = false;
    }

    private static bool TryGetLocalPlayer(Collider other, out PlayerDeath death)
    {
        return FusionPlayers.TryGetPlayer(other, out _, out death) && death.IsLocalPlayer;
    }

    private static readonly List<Collider> s_PlayerColliders = new();

    /// <summary>ตัวละครยังซ้อนโซนอยู่จริงไหม (เทียบ bounds) — collider ที่ถูกปิดไม่นับ</summary>
    public static bool StillOverlaps(Collider zone, Component player)
    {
        if (zone == null) return true;
        if (player == null) return false;
        Bounds zb = zone.bounds;
        player.GetComponentsInChildren(false, s_PlayerColliders);
        foreach (var c in s_PlayerColliders)
        {
            if (c.enabled && !c.isTrigger && c.bounds.Intersects(zb)) return true;
        }
        return false;
    }
}
