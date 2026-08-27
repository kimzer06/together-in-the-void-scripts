using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using System.Linq;
using UnityEngine.SceneManagement;

public class RelayMenuSingleScene : MonoBehaviour
{
    [Header("UI (optional)")]
    [SerializeField] private GameObject joinPanel;                 // ไม่ใส่ก็ได้
    [SerializeField] private TMPro.TMP_InputField joinCodeInput;   // ไม่ใส่ก็ได้

    [Header("Network")]
    [SerializeField] private UnityTransport transport;
    [SerializeField] private int maxConnections = 3;
    [SerializeField] private bool useDtls = false;

    [Header("Scene Settings")]
    [SerializeField] private string characterSelectSceneName = "CharacterSelect";

    private bool inited;

    private void Awake()
    {
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        if (NetworkManager.Singleton && NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.OnSceneEvent += (e) =>
                Debug.Log($"[NGO Scene] {e.SceneEventType} : {e.SceneName} (server={NetworkManager.Singleton.IsServer})");
        }
    }

    private async Task EnsureServicesAsync()
    {
        if (inited) return;

        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        if (!transport) transport = FindObjectOfType<UnityTransport>();
        if (!transport) Debug.LogError("UnityTransport not found in scene.");

        inited = true;
    }

    // ---------- HOST ----------
    public async void HostRelay()
    {
        try
        {
            await EnsureServicesAsync();

            var regions = await RelayService.Instance.ListRegionsAsync();
            var region = regions.FirstOrDefault(x => x.Id == "ap-southeast1") ?? regions[0];

            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxConnections, region.Id);
            string code = (await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId)).ToUpperInvariant();

            // cache ไว้เพื่อโชว์ทันที
            LobbySelectionState.CachedJoinCode = code;
            Debug.Log($"[Relay] Host join code: {code}");

            transport.SetRelayServerData(
                alloc.RelayServer.IpV4,
                (ushort)alloc.RelayServer.Port,
                alloc.AllocationIdBytes,
                alloc.Key,
                alloc.ConnectionData,
                System.Array.Empty<byte>(),
                useDtls
            );

            bool ok = NetworkManager.Singleton.StartHost();
            Debug.Log($"[Relay] StartHost()={ok}, EnableSceneManagement={NetworkManager.Singleton.NetworkConfig.EnableSceneManagement}");
            if (!ok) return;

            NetworkManager.Singleton.SceneManager.LoadScene(characterSelectSceneName, LoadSceneMode.Single);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"Relay Host error: {e}");
        }
    }

    // ---------- CLIENT ----------
    public async void JoinRelay()
    {
        try
        {
            await EnsureServicesAsync();

            string code = joinCodeInput ? joinCodeInput.text.Trim().ToUpperInvariant() : "";
            if (string.IsNullOrWhiteSpace(code))
            {
                Debug.LogError("Join code is empty.");
                return;
            }

            LobbySelectionState.CachedJoinCode = code; // ให้โชว์ทันทีฝั่ง client

            JoinAllocation joinAlloc = await RelayService.Instance.JoinAllocationAsync(code);

            transport.SetRelayServerData(
                joinAlloc.RelayServer.IpV4,
                (ushort)joinAlloc.RelayServer.Port,
                joinAlloc.AllocationIdBytes,
                joinAlloc.Key,
                joinAlloc.ConnectionData,
                joinAlloc.HostConnectionData,
                useDtls
            );

            bool ok = NetworkManager.Singleton.StartClient();
            Debug.Log($"[Relay] StartClient()={ok}. Waiting host to switch scene...");
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"Relay Client error: {e}");
        }
    }
}
