using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Splatoon.Loading
{
    public readonly struct SceneLoadHandle
    {
        public readonly UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<SceneInstance> Operation;
        public SceneInstance Scene => Operation.Result;
        public SceneLoadHandle(UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<SceneInstance> operation) => Operation = operation;
    }
    public interface ISceneLoader
    {
        UniTask<SceneLoadHandle> LoadAsync(string address, IProgress<float> progress, System.Threading.CancellationToken cancellationToken);
        UniTask UnloadAsync(SceneLoadHandle handle);
    }
}
