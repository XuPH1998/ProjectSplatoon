using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace Splatoon.Loading
{
    public readonly struct SceneLoadHandle { public readonly SceneInstance Scene; public SceneLoadHandle(SceneInstance scene) => Scene = scene; }
    public interface ISceneLoader
    {
        UniTask<SceneLoadHandle> LoadAsync(string address, IProgress<float> progress, System.Threading.CancellationToken cancellationToken);
        UniTask UnloadAsync(SceneLoadHandle handle);
    }
}
