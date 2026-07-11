namespace Structura.Web.Domain;

public class AppSetting
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = "";
    public bool IsProtected { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
