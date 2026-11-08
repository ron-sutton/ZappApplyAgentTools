using System.ComponentModel.DataAnnotations;

namespace ZappApplyAgentTools;

public sealed class SourcePosting
{
    [Required, MaxLength(2048)] public string Url { get; set; } = "";
    [Required, MaxLength(300)] public string Title { get; set; } = "";
    [Required, MaxLength(300)] public string Company { get; set; } = "";
    [Required, MaxLength(100000)] public string Description { get; set; } = "";
    [Required, MaxLength(100)] public string Source { get; set; } = "";
    [MaxLength(500)] public string? ExternalId { get; set; }
    [MaxLength(2048)] public string? CompanyLogoUrl { get; set; }
    [MaxLength(2048)] public string? CompanyWebsiteUrl { get; set; }
    [MaxLength(500)] public string? Location { get; set; }
    [RegularExpression("remote|hybrid|onsite")] public string? WorkArrangement { get; set; }
    [MaxLength(100)] public List<string> EligibleCountries { get; set; } = [];
    [Range(0, 100000000)] public decimal? SalaryMin { get; set; }
    [Range(0, 100000000)] public decimal? SalaryMax { get; set; }
    [RegularExpression("[A-Z]{3}")] public string? Currency { get; set; }
    [RegularExpression("annual|hourly")] public string? PayPeriod { get; set; }
    [RegularExpression("base|total")] public string? CompensationBasis { get; set; }
    [MaxLength(100)] public string? EmploymentType { get; set; }
    [MaxLength(100)] public string? Seniority { get; set; }
    [MaxLength(100)] public string? Industry { get; set; }
    public bool? OffersSponsorship { get; set; }
    public bool? IsStaffingAgency { get; set; }
    public bool? RequiresClearance { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public bool Expired { get; set; }
}
