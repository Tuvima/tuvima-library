using System.Net;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Web.Tests;

public sealed class OriginalClientContextTests
{
    private static IngressKind Classify(HttpContext context) => new IngressClassifier(null, []).Classify(context);
}
