﻿using Newtonsoft.Json;

namespace Registry.Ports.DroneDB.Models;

public class Delta
{
    [JsonProperty("adds")]
    public AddAction[] Adds { get; set; }

    [JsonProperty("removes")]
    public RemoveAction[] Removes { get; set; }

    [JsonProperty("metaAdds")]
    public string[] MetaAdds { get; set; }
        
    [JsonProperty("metaRemoves")]
    public string[] MetaRemoves { get; set; }


}