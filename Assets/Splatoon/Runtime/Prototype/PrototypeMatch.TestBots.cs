using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        public string TestBotMessage { get; private set; } = "";
        public int TestBotCount => Players.Count(p => p != null && p.IsSpawned && p.IsTestBot);
        public bool CanAddTestBot => IsHost && State.Value.Phase == MatchPhase.Practice && PrototypePlayer.Local != null &&
            Players.Count(p => p != null && p.IsSpawned) < GameplayConfig.Mode.MaxPlayers &&
            TeamSelectionRules.TryFindSlot((byte)(3 - PrototypePlayer.Local.Snapshot.Value.Team),
                Players.Where(p => p != null && p.IsSpawned).Select(p => p.Snapshot.Value), out _);

        public PrototypePlayer AddTestBot(GameObject prefab)
        {
            if (!CanAddTestBot || prefab == null) { TestBotMessage = "仅房主可在热身添加 BOT，敌队最多 4 人。"; return null; }
            var host = PrototypePlayer.Local.Snapshot.Value;
            byte team = (byte)(3 - host.Team);
            TeamSelectionRules.TryFindSlot(team, Players.Where(p => p != null && p.IsSpawned).Select(p => p.Snapshot.Value), out byte slot);
            var shape = prefab.GetComponent<CharacterController>();
            if (!TryTestBotPosition(host, shape, out var position))
            { TestBotMessage = "前方没有可站立的空地，请换个位置召唤。"; return null; }
            var go = Instantiate(prefab, position, Quaternion.identity);
            var bot = go.GetComponent<PrototypePlayer>();
            bot.InitializeTestBot(team, slot, host.HeroId, position, Mathf.Repeat(host.Yaw + 180, 360));
            go.GetComponent<NetworkObject>().Spawn(true); Players.Add(bot);
            TestBotMessage = "已在前方召唤静止敌方 BOT。";
            return bot;
        }

        bool TryTestBotPosition(PlayerSnapshot host, CharacterController shape, out Vector3 position)
        {
            var aim = Quaternion.Euler(0, host.Yaw, 0);
            foreach (float distance in new[] { 6f, 4f, 8f }) foreach (float side in new[] { 0f, 1.5f, -1.5f, 3f, -3f })
            {
                var candidate = host.Position + aim * new Vector3(side, 0, distance);
                var origin = host.Position + Vector3.up * .5f;
                if (Physics.Linecast(origin, candidate + Vector3.up * .5f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) continue;
                if (!Physics.Raycast(candidate + Vector3.up * 2, Vector3.down, out var ground, 4,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) || ground.normal.y < .7f) continue;
                position = ground.point + Vector3.up * .04f;
                var center = position + shape.center; float radius = Mathf.Max(.05f, shape.radius - .025f);
                if (Physics.CheckCapsule(center + Vector3.up * (shape.height / 2 - radius),
                    center - Vector3.up * (shape.height / 2 - radius), radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) continue;
                var spawnPosition = position;
                if (Players.Any(p => p != null && Vector3.Distance(p.Snapshot.Value.Position, spawnPosition) < shape.radius * 2 + .2f)) continue;
                return true;
            }
            position = default; return false;
        }

        public void ClearTestBots()
        {
            if (!IsHost || State.Value.Phase != MatchPhase.Practice) return;
            foreach (var bot in Players.Where(p => p != null && p.IsSpawned && p.IsTestBot).ToArray())
                RemoveTestBot(bot);
            TestBotMessage = "已移除测试 BOT。";
        }
        void RemoveTestBot(PrototypePlayer bot)
        {
            ulong id = bot.PlayerId;
            Players.Remove(bot); CombatStats.Remove(id); bot.NetworkObject.Despawn(true);
        }
    }
}
