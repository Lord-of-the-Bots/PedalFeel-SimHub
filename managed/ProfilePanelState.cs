using System.Collections.Generic;

namespace PedalFeel.SimHub
{
    public sealed class ProfilePanelState
    {
        public List<KeyValuePair<string, string>> Profiles { get; set; } = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> Bases { get; set; } = new List<KeyValuePair<string, string>>();
        public string SelectedId { get; set; } = "";
        public string AssignedId { get; set; } = "";
        public string CurrentCar { get; set; } = "";
        public string Description { get; set; } = "";
        public bool CanAssign { get; set; }
    }
}
