using System.IO;
using System.Text;
using SimpleJSON;

namespace Luban
{
    public abstract class EditorBeanBase
    {
        public abstract void LoadJson(JSONObject json);

        public abstract void SaveJson(JSONObject json);

        public void LoadJsonFile(string file)
        {
            string jsonText = File.ReadAllText(file, Encoding.UTF8);
            LoadJson((JSONObject)JSON.Parse(jsonText));
        }

        public void SaveJsonFile(string file)
        {
            var json = new JSONObject();
            SaveJson(json);
            File.WriteAllText(file, json.ToString(), Encoding.UTF8);
        }
    }
}