using UnityEngine;
using Unity.Netcode;
using DG.Tweening;

[RequireComponent(typeof(NetworkObject))]
// [RequireComponent(typeof(Unity.Netcode.Components.NetworkTransform))]
public class MovingPlatform : NetworkBehaviour
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    [Header("Platform Movement Settings")]
    [SerializeField] private Axis moveAxis = Axis.X;
    [SerializeField, Min(0f)] private float moveAmount = 3f;
    [SerializeField] private Direction direction = Direction.Positive;
    [SerializeField] private float moveDuration = 2f;
    [SerializeField] private Ease moveEase = Ease.InOutSine;
    [SerializeField] private bool useLocalSpace = true;

    [Header("Debug")]
    [SerializeField] private bool showGizmos = true;

    Vector3 startPosLocal, startPosWorld;
    Vector3 endPos;
    Sequence seq;

    void Awake()
    {
        startPosLocal = transform.localPosition;
        startPosWorld = transform.position;
        RecalculateEnd();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return; // เซิร์ฟเวอร์คุม motion
        StartMoveLoop();
    }

    void RecalculateEnd()
    {
        Vector3 axis = moveAxis == Axis.X ? Vector3.right :
                       moveAxis == Axis.Y ? Vector3.up : Vector3.forward;

        float dir = (direction == Direction.Positive) ? 1f : -1f;

        if (useLocalSpace)
            endPos = startPosLocal + axis * (moveAmount * dir);
        else
            endPos = startPosWorld + axis * (moveAmount * dir);
    }

    void StartMoveLoop()
    {
        seq?.Kill();
        // สำคัญ: ผูก Id/Target ให้เป็น transform/gameObject
        seq = DOTween.Sequence()
             .SetId(gameObject)
             .SetTarget(transform)
             .SetLoops(-1, LoopType.Restart);

        if (useLocalSpace)
        {
            transform.localPosition = startPosLocal;
            seq.Append(transform.DOLocalMove(endPos, moveDuration).SetEase(moveEase));
            seq.Append(transform.DOLocalMove(startPosLocal, moveDuration).SetEase(moveEase));
        }
        else
        {
            transform.position = startPosWorld;
            seq.Append(transform.DOMove(endPos, moveDuration).SetEase(moveEase));
            seq.Append(transform.DOMove(useLocalSpace ? startPosLocal : startPosWorld, moveDuration).SetEase(moveEase));
        }
    }

    void OnDisable() { seq?.Kill(); }
    void OnDestroy() { seq?.Kill(); }

    void OnValidate()
    {
        if (Application.isPlaying) return;
        startPosLocal = transform.localPosition;
        startPosWorld = transform.position;
        RecalculateEnd();
    }

    void OnDrawGizmos()
    {
        if (!showGizmos) return;

        Vector3 start = useLocalSpace
            ? (Application.isPlaying ? startPosLocal : transform.localPosition)
            : (Application.isPlaying ? startPosWorld : transform.position);

        Gizmos.color = Color.green; Gizmos.DrawLine(start, endPos);
        Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(start, 0.15f);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(endPos, 0.15f);
    }
}