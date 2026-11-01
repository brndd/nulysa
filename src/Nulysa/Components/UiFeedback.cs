using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Nulysa.Components;

public sealed class UiFeedback(ITempDataDictionaryFactory factory)
{
    public string? Read(HttpContext context)
    {
        var data = factory.GetTempData(context);
        var notice = data["UiNotice"] as string;
        data.Save();
        return notice;
    }

    public void Save(HttpContext context, string notice)
    {
        var data = factory.GetTempData(context);
        data["UiNotice"] = notice;
        data.Save();
    }
}
