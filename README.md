# ConditionalCompilationUtility
The Conditional Compilation Utility (CCU) will add defines to the build settings once dependendent classes have been detected. 
A goal of the CCU was to not require the CCU itself for other libraries to specify optional dependencies. So, it relies on 
the specification of at least one custom attribute in a project that makes use of it. Here is an example:

```
[Conditional(UNITY_CCU)]                                    // | This is necessary for CCU to pick up the right attributes
public class OptionalDependencyAttribute : Attribute        // | Must derive from System.Attribute
{
    public string dependentClass;                           // | Required field specifying the fully qualified dependent class
    public string define;                                   // | Required field specifying the define to add
}
```

Then, simply specify the assembly attribute(s) you created in any of your C# files:
```
[assembly: OptionalDependency("UnityEngine.InputNew.InputSystem", "USE_NEW_INPUT")]
[assembly: OptionalDependency("Valve.VR.IVRSystem", "ENABLE_STEAMVR_INPUT")]

namespace Foo
{
...
}
```

This allows a separate project on GitHub, for example, to define optional dependencies independently. And if CCU is present, then 
the defines will automatically be added upon detection of dependent classes. If the CCU is not present, then the project would 
still work, but may require the developer to add defines manually.
