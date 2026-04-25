#nullable enable
using System.Collections.Generic;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Built-in material catalog used to estimate stockpile weight and cost
/// from a computed volume. Values are reasonable defaults; projects that
/// need precise numbers should override them via their own workflow.
/// </summary>
public static class StockpileMaterials
{
    public static readonly IReadOnlyList<MaterialInfoDto> All = new[]
    {
        new MaterialInfoDto { Slug = "gravel",     Name = "Gravel",          Category = "Aggregate",  DensityTonPerM3 = 1.68, CostPerTon = 15.0  },
        new MaterialInfoDto { Slug = "sand",       Name = "Sand",            Category = "Aggregate",  DensityTonPerM3 = 1.60, CostPerTon = 12.0  },
        new MaterialInfoDto { Slug = "topsoil",    Name = "Topsoil",         Category = "Soil",       DensityTonPerM3 = 1.30, CostPerTon = 20.0  },
        new MaterialInfoDto { Slug = "clay",       Name = "Clay",            Category = "Soil",       DensityTonPerM3 = 1.70, CostPerTon = 10.0  },
        new MaterialInfoDto { Slug = "rock",       Name = "Crushed Rock",    Category = "Aggregate",  DensityTonPerM3 = 1.60, CostPerTon = 18.0  },
        new MaterialInfoDto { Slug = "asphalt",    Name = "Asphalt",         Category = "Paving",     DensityTonPerM3 = 2.24, CostPerTon = 95.0  },
        new MaterialInfoDto { Slug = "concrete",   Name = "Concrete",        Category = "Paving",     DensityTonPerM3 = 2.40, CostPerTon = 85.0  },
        new MaterialInfoDto { Slug = "coal",       Name = "Coal",            Category = "Mining",     DensityTonPerM3 = 0.85, CostPerTon = 70.0  },
        new MaterialInfoDto { Slug = "iron_ore",   Name = "Iron Ore",        Category = "Mining",     DensityTonPerM3 = 2.50, CostPerTon = 110.0 },
        new MaterialInfoDto { Slug = "limestone",  Name = "Limestone",       Category = "Aggregate",  DensityTonPerM3 = 1.55, CostPerTon = 16.0  },
        new MaterialInfoDto { Slug = "wood_chips", Name = "Wood Chips",      Category = "Organic",    DensityTonPerM3 = 0.30, CostPerTon = 40.0  },
        new MaterialInfoDto { Slug = "snow",       Name = "Snow",            Category = "Other",      DensityTonPerM3 = 0.30, CostPerTon = 0.0   },
        new MaterialInfoDto { Slug = "waste",      Name = "Mixed Waste",     Category = "Waste",      DensityTonPerM3 = 0.80, CostPerTon = 50.0  },
        new MaterialInfoDto { Slug = "compost",    Name = "Compost",         Category = "Organic",    DensityTonPerM3 = 0.55, CostPerTon = 45.0  },
        new MaterialInfoDto { Slug = "fly_ash",    Name = "Fly Ash",         Category = "Industrial", DensityTonPerM3 = 1.00, CostPerTon = 25.0  }
    };

    public static MaterialInfoDto? FindBySlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        foreach (var m in All)
            if (string.Equals(m.Slug, slug, System.StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }
}
