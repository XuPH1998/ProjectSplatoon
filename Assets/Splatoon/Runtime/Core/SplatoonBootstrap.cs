using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon
{
    public sealed class SplatoonBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Create()
        {
#if UNITY_EDITOR
            // This art-only scene evaluates the original character animations
            // without starting the lobby, Addressables or network services.
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "RifleGirlChibiPreview") return;
#endif
            if (FindFirstObjectByType<PrototypeApp>() != null) return;
            var go = new GameObject("SplatoonBootstrap");
            DontDestroyOnLoad(go);
            go.AddComponent<PrototypeApp>();
        }
    }
}
