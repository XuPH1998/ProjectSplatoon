using UnityEngine;

namespace Splatoon.Skills
{
    public sealed class SkillRunner : MonoBehaviour
    {
        [SerializeField] private SkillDefinition definition; [SerializeField] private MonoBehaviour handlerComponent; private ISkillEventHandler _handler; private float _time; private bool _playing;
        public SkillDefinition Definition { get => definition; set => definition = value; }
        private void Awake() => _handler = handlerComponent as ISkillEventHandler;
        public void Play() { _time = 0f; _playing = definition != null; }
        private void Update() { if (!_playing || definition == null) return; _time += Time.deltaTime; foreach (var clip in definition.Clips) if (clip != null && Mathf.Abs(_time - clip.StartTime) < Time.deltaTime * 1.5f) _handler?.Handle(new SkillEventData(clip.EventType, clip.StartTime)); if (_time >= definition.ResolveDuration()) _playing = false; }
    }
}
