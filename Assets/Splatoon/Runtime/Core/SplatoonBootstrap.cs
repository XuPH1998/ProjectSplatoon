using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon
{
    public sealed class SplatoonBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Create()
        {
            if (FindFirstObjectByType<PrototypeApp>() != null) return;
            var go = new GameObject("SplatoonBootstrap");
            DontDestroyOnLoad(go);
            go.AddComponent<PrototypeApp>();
        }
    }
}
