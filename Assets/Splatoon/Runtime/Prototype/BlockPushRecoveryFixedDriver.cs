#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Test-only fixed-loop scheduler. It neither simulates PhysX nor changes production physics settings.</summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(0)]
    public sealed class BlockPushRecoveryFixedDriver : MonoBehaviour
    {
        public Func<bool> OnFixedStep;
        public Exception Failure;

        void FixedUpdate()
        {
            try
            {
                if (OnFixedStep != null && OnFixedStep()) enabled = false;
            }
            catch (Exception exception)
            {
                Failure = exception;
                enabled = false;
            }
        }
    }
}
#endif
