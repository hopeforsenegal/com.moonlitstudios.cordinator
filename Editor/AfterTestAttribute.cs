[System.AttributeUsage(System.AttributeTargets.Method)]
public class AfterTestAttribute : System.Attribute { } // Because we want to call a build script after our tests (and this can't be done in playmode) 