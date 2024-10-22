using System;
using Polly;

namespace Registry.Web;

public static class Policies
{
    public static readonly Policy Base = Policy.Handle<Exception>().Retry(3);
}