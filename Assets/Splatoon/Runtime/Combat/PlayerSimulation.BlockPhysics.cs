using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed partial class PlayerMotorSimulation
    {
        PlayerBlockPushBridge _blockPush;

        PlayerBlockPushBridge BeginBlockMovement(float dt)
        {
            if (_blockPush == null)
                _blockPush = _root.GetComponent<PlayerBlockPushBridge>() ?? _root.gameObject.AddComponent<PlayerBlockPushBridge>();
            _blockPush.BeginStep(_root.GetComponent<PrototypePlayer>(), dt);
            return _blockPush;
        }

        CollisionFlags MoveWithFreeBlocks(Vector3 displacement, float dt)
            => _blockPush != null ? _blockPush.Move(displacement, dt) : _controller.Move(displacement);
    }
}
