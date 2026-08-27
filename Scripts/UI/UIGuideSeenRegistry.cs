using UnityEngine;
using System.Collections.Generic;

public class UIGuideSeenRegistry : MonoBehaviour
{
    private readonly HashSet<string> _seen = new HashSet<string>();

    public bool HasSeen(string id)
    {
        return !string.IsNullOrEmpty(id) && _seen.Contains(id);
    }

    public void MarkSeen(string id)
    {
        if (!string.IsNullOrEmpty(id))
            _seen.Add(id);
    }
}
