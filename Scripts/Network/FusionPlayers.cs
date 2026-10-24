using Fusion;
using UnityEngine;

/// <summary>
/// ตัวช่วยหา "ผู้เล่น" สำหรับของในโลก (สวิตช์/โซน) — แทน NetworkObject.IsPlayerObject + OwnerClientId ของ NGO
/// Shared Mode: เจ้าของตัวละคร = state authority ของ NetworkObject ตัวละครนั้น
/// </summary>
public static class FusionPlayers
{
    /// <summary>
    /// collider นี้เป็นของตัวละครผู้เล่นหรือไม่ ถ้าใช่คืน PlayerRef ของเจ้าของ
    /// ใช้ PlayerDeath เป็นตัวบอกว่าเป็นตัวละคร (มีทุก player prefab) — ร่างตาย/กระสุนที่ผู้เล่นเป็นเจ้าของจะไม่นับ
    /// </summary>
    public static bool TryGetPlayer(Collider c, out PlayerRef player, out PlayerDeath death)
    {
        player = PlayerRef.None;
        death = c ? c.GetComponentInParent<PlayerDeath>() : null;
        if (death == null || death.Object == null || !death.Object.IsValid) return false;
        player = death.Object.StateAuthority;
        return true;
    }

    public static bool TryGetPlayer(Collider c, out PlayerRef player) => TryGetPlayer(c, out player, out _);

    /// <summary>
    /// ใน hits[0..count) (ผลจาก Physics.Overlap*NonAlloc) มีตัวละครของ player คนนี้ไหม
    /// requiredTag ว่าง = ไม่กรอง tag
    /// </summary>
    public static bool ContainsPlayer(Collider[] hits, int count, string requiredTag, PlayerRef player)
    {
        if (player == PlayerRef.None) return false;
        for (int i = 0; i < count; i++)
        {
            if (PassesTag(hits[i], requiredTag) && TryGetPlayer(hits[i], out PlayerRef p) && p == player)
                return true;
        }
        return false;
    }

    /// <summary>
    /// ตัวละครของผู้เล่นคนนี้ (เครื่องไหนก็เรียกได้) — แทน NetworkManager.ConnectedClients[id].PlayerObject ของ NGO
    /// ลอง player object ที่ runner รู้จักก่อน ไม่เจอค่อยไล่หา PlayerDeath ที่ผู้เล่นคนนั้นเป็นเจ้าของ
    /// </summary>
    public static bool TryGetPlayerDeath(NetworkRunner runner, PlayerRef player, out PlayerDeath death)
    {
        death = null;
        if (runner == null || player == PlayerRef.None) return false;

        if (runner.TryGetPlayerObject(player, out var no) && no != null && no.TryGetComponent(out death))
            return true;

        foreach (var d in Object.FindObjectsByType<PlayerDeath>(FindObjectsSortMode.None))
        {
            if (d.Object != null && d.Object.IsValid && d.Object.StateAuthority == player)
            {
                death = d;
                return true;
            }
        }
        return false;
    }

    /// <summary>ผู้เล่นคนนี้ยังอยู่ใน session ไหม (แทน ConnectedClients.ContainsKey)</summary>
    public static bool IsActive(NetworkRunner runner, PlayerRef player)
    {
        if (runner == null || player == PlayerRef.None) return false;
        foreach (var p in runner.ActivePlayers)
            if (p == player) return true;
        return false;
    }

    private static bool PassesTag(Collider c, string requiredTag) =>
        c && (string.IsNullOrEmpty(requiredTag) || c.CompareTag(requiredTag));

    /// <summary>
    /// คนที่ส่ง RPC นี้มา — ถ้าเครื่องที่ถือ state authority เรียก RPC ของตัวเอง (invoke local) ใช้ LocalPlayer
    /// </summary>
    public static PlayerRef SenderOf(NetworkRunner runner, RpcInfo info)
    {
        if (info.IsInvokeLocal || info.Source == PlayerRef.None)
            return runner != null ? runner.LocalPlayer : PlayerRef.None;
        return info.Source;
    }
}
