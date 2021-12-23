namespace Registry.Ports.DroneDB.Models
{
    public class Delta
    {
        public AddAction[] Adds { get; set; }

        public RemoveAction[] Removes { get; set; }
    }
}