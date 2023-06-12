﻿using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Razor.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace RazorSlices;

/// <summary>
/// Contains methods for creating instances of slices.
/// </summary>
/// <remarks>
/// You generally shouldn't have to call methods on this class directly.
/// </remarks>
public static class RazorSliceFactory
{
    private static readonly HashSet<string> ExcludedServiceNames =
        new(StringComparer.OrdinalIgnoreCase) { "IModelExpressionProvider", "IUrlHelper", "IViewComponentHelper", "IJsonHelper", "IHtmlHelper`1" };

    //private static readonly Type _serviceProviderType = typeof(IServiceProvider);
    //private static readonly Type _serviceProviderExtensionsType = typeof(ServiceProviderServiceExtensions);
    //private static readonly MethodInfo _getServiceMethodInfo = _serviceProviderType
    //    .GetMethod(nameof(IServiceProvider.GetService), new[] { typeof(Type) })!;
    //private static readonly MethodInfo _getRequiredServiceMethodInfo = _serviceProviderExtensionsType
    //    .GetMethod(nameof(ServiceProviderServiceExtensions.GetRequiredService), new[] { _serviceProviderType, typeof(Type) })!;
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static readonly Type _razorSliceType = typeof(RazorSlice);
    private static readonly PropertyInfo _razorSliceInitializeProperty = _razorSliceType.GetProperty(nameof(RazorSlice.Initialize))!;
    private static readonly ConstructorInfo _ioeCtor = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!;
    private static readonly NullabilityInfoContext _nullabilityContext = new();
    private static readonly Action<RazorSlice, IServiceProvider?> _emptyInit = (_, __) => { };

    /// <summary>
    /// 
    /// </summary>
    /// <param name="sliceType"></param>
    /// <returns></returns>
    internal static bool IsModelSlice(Type sliceType)
    {
        var baseType = sliceType.BaseType;

        while (baseType is not null)
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(RazorSlice<>))
            {
                return true;
            }

            baseType = baseType.BaseType;
        }

        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="sliceType"></param>
    /// <returns></returns>
    internal static (bool Any, PropertyInfo[] Nullable, PropertyInfo[] NonNullable) GetInjectableProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        Type sliceType)
    {
        List<PropertyInfo>? nullable = null;
        List<PropertyInfo>? nonNullable = null;

        foreach (var pi in sliceType.GetProperties())
        {
            if (pi.GetCustomAttribute<RazorInjectAttribute>() is not null
                && !ExcludedServiceNames.Contains(pi.PropertyType.Name))
            {
                if (IsNullable(pi))
                {
                    nullable ??= new();
                    nullable.Add(pi);
                }
                else
                {
                    nonNullable ??= new();
                    nonNullable.Add(pi);
                }
            }
        }

        return (nullable is not null || nonNullable is not null,
                nullable?.ToArray() ?? Array.Empty<PropertyInfo>(),
                nonNullable?.ToArray() ?? Array.Empty<PropertyInfo>());
    }

    private static Action<RazorSlice, IServiceProvider?> GetReflectionInitAction(SliceDefinition sliceDefinition)
    {
        return sliceDefinition.InjectableProperties.Any
            ? (s, sp) =>
            {
                if (sp is null)
                {
                    throw new InvalidOperationException($"Cannot initialize @inject properties of slice {sliceDefinition.SliceType.Name} because the ServiceProvider property is null.");
                }
                foreach (var pi in sliceDefinition.InjectableProperties.NonNullable)
                {
                    pi.SetValue(s, sp.GetRequiredService(pi.PropertyType));
                }
                foreach (var pi in sliceDefinition.InjectableProperties.Nullable)
                {
                    pi.SetValue(s, sp.GetService(pi.PropertyType));
                }
            }
            : _emptyInit;
    }

#if NET7_0_OR_GREATER
    [RequiresDynamicCode("Uses System.Linq.Expressions to dynamically generate delegates for initializing slices")]
#endif
    private static Expression<Action<RazorSlice, IServiceProvider?>> GetExpressionInitAction(SliceDefinition sliceDefinition)
    {
        if (!sliceDefinition.InjectableProperties.Any) throw new InvalidOperationException("Shouldn't call GetExpressionInitAction if there's no injectable properties.");

        // (RazorSlice slice, IServiceProvider? sp) =>
        // {
        //     if (sp is null) throw new InvalidOperationException("Cannot initialize @inject properties of slice because the ServiceProvider property is null.");
        //     var s = (MySlice)slice;
        //     s.SomeProp = (SomeService)sp.GetService(typeof(SomeService));
        //     s.NextProp = (SomeOtherService)sp.GetRequiredService(typeof(SomeOtherService));
        // }

        var sliceParam = Expression.Parameter(_razorSliceType, "slice");
        var spParam = Expression.Parameter(typeof(IServiceProvider), "sp");
        var castSliceVar = Expression.Variable(sliceDefinition.SliceType, "s");

        var body = new List<Expression>
        {
            Expression.IfThen(
                Expression.Equal(spParam, Expression.Constant(null)),
                Expression.Throw(Expression.New(_ioeCtor, Expression.Constant("Cannot initialize @inject properties of slice because the ServiceProvider property is null.")))),
            Expression.Assign(castSliceVar, Expression.Convert(sliceParam, sliceDefinition.SliceType))
        };

        var getServiceMethod = typeof(IServiceProvider).GetMethod("GetService")!;
        foreach (var ip in sliceDefinition.InjectableProperties.Nullable)
        {
            var propertyAccess = Expression.MakeMemberAccess(castSliceVar, ip);
            var getServiceCall = Expression.Call(spParam, getServiceMethod, Expression.Constant(ip.PropertyType));
            body.Add(Expression.Assign(propertyAccess, Expression.Convert(getServiceCall, ip.PropertyType)));
        }

        var getRequiredServiceMethod = typeof(ServiceProviderServiceExtensions).GetMethod("GetRequiredService", new[] { typeof(IServiceProvider), typeof(Type) })!;
        foreach (var ip in sliceDefinition.InjectableProperties.NonNullable)
        {
            var propertyAccess = Expression.MakeMemberAccess(castSliceVar, ip);
            var getServiceCall = Expression.Call(null, getRequiredServiceMethod, spParam, Expression.Constant(ip.PropertyType));
            body.Add(Expression.Assign(propertyAccess, Expression.Convert(getServiceCall, ip.PropertyType)));
        }

        return Expression.Lambda<Action<RazorSlice, IServiceProvider?>>(
            body: Expression.Block(
                variables: new[] { castSliceVar },
                body),
            parameters: new[] { sliceParam, spParam });
    }

    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.",
        Justification = "Guarded by check of RuntimeFeature.IsDynamicCodeCompiled")]
    internal static Delegate GetSliceFactory(SliceDefinition sliceDefinition)
    {
        return RuntimeFeature.IsDynamicCodeCompiled
            ? GetExpressionsSliceFactory(sliceDefinition)
            : GetReflectionSliceFactory(sliceDefinition);
    }

    private static Delegate GetReflectionSliceFactory(SliceDefinition sliceDefinition)
    {
        var init = GetReflectionInitAction(sliceDefinition);
        return sliceDefinition.HasModel
            ? (object model) =>
            {
                var slice = (RazorSlice)Activator.CreateInstance(sliceDefinition.SliceType)!;
                sliceDefinition.ModelProperty!.SetValue(slice, model);
                slice.Initialize = init;
                return slice;
            }
            : () =>
            {
                var slice = (RazorSlice)Activator.CreateInstance(sliceDefinition.SliceType)!;
                slice.Initialize = init;
                return slice;
            };
    }

    /// <summary>
    /// Creates a <see cref="SliceFactory"/> that can be used to create a <see cref="RazorSlice"/> of the specified <see cref="Type"/>.
    /// </summary>
    /// <param name="sliceDefinition"></param>
    /// <returns>A <see cref="SliceFactory"/> that can be used to create an instance of the slice.</returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("Uses System.Linq.Expressions to dynamically generate delegates for creating slices")]
#endif
    private static Delegate GetExpressionsSliceFactory(SliceDefinition sliceDefinition)
    {
        var sliceType = sliceDefinition.SliceType;

        if (sliceType.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new ArgumentException($"Slice type {sliceType.Name} must have a parameterless constructor.", nameof(sliceDefinition));
        }

        var body = new List<Expression>();

        // Make a delegate like:
        //
        // MySlice CreateSlice()
        // {
        //     var slice = new SliceType();
        //     slice.Init = ...;
        //     return slice;
        // }
        // or
        // MySlice CreateSlice(object model)
        // {
        //     var slice = new SliceType<MyModel>();
        //     slice.Init = ...;
        //     slice.Model = (MyModel)model
        //     return slice;
        // }

        var sliceVariable = Expression.Variable(sliceType, "slice");
        body.Add(Expression.Assign(sliceVariable, Expression.New(sliceType)));
        ParameterExpression[]? parameters = null;
        var factoryType = typeof(Func<RazorSlice>);

        if (sliceDefinition.InjectableProperties.Any)
        {
            body.Add(Expression.Assign(
                Expression.MakeMemberAccess(sliceVariable, _razorSliceInitializeProperty),
                GetExpressionInitAction(sliceDefinition)!));
        }

        if (sliceDefinition.ModelType is not null)
        {
            // Func<object, RazorSlice<MyModel>>
            var modelPropInfo = sliceType.GetProperty("Model")!;
            factoryType = typeof(Func<,>).MakeGenericType(typeof(object), sliceType);
            var modelParam = Expression.Parameter(typeof(object), "model");
            parameters = new[] { modelParam };
            body.Add(Expression.Assign(
                Expression.MakeMemberAccess(sliceVariable, modelPropInfo),
                Expression.Convert(modelParam, sliceDefinition.ModelType)));
        }

        var returnTarget = Expression.Label(sliceType);
        body.Add(Expression.Label(returnTarget, sliceVariable));

        return Expression.Lambda(
            delegateType: factoryType,
            body: Expression.Block(
                variables: new[] { sliceVariable },
                body
            ),
            name: "CreateSlice",
            parameters: parameters)
        .Compile();
    }

    private static bool IsNullable(PropertyInfo info) =>
        Nullable.GetUnderlyingType(info.PropertyType) is not null
            || _nullabilityContext.Create(info).WriteState is not NullabilityState.NotNull;

    /// <summary>
    /// Creates an instance of a <see cref="RazorSlice" /> template using the provided <see cref="SliceFactory" /> delegate.
    /// </summary>
    /// <param name="sliceFactory">The <see cref="SliceFactory" /> delegate to create the template with.</param>
    /// <returns>A <see cref="RazorSlice" /> instance for the template.</returns>
    public static RazorSlice Create(SliceFactory sliceFactory) => sliceFactory();

    /// <summary>
    /// Creates an instance of a <see cref="RazorSlice" /> template using the provided <see cref="SliceFactory" /> delegate with a typed model.
    /// </summary>
    /// <param name="sliceFactory">The <see cref="SliceFactory" /> delegate to create the template with.</param>
    /// <param name="model">The model to use for the template instance.</param>
    /// <typeparam name="TModel">The model type of the template.</typeparam>
    /// <returns>A <see cref="RazorSlice{TModel}" /> instance for the template.</returns>
    public static RazorSlice<TModel> Create<TModel>(SliceFactory<TModel> sliceFactory, TModel model) => sliceFactory(model);

    /// <summary>
    /// Creates and returns a new instance of a <see cref="RazorSliceHttpResult" /> using the provided <see cref="SliceFactory" /> delegate.
    /// </summary>
    /// <param name="sliceFactory">The <see cref="SliceFactory" /> delegate.</param>
    /// <param name="statusCode">The HTTP status code to return. Defaults to <see cref="StatusCodes.Status200OK"/>.</param>
    /// <returns>The <see cref="RazorSliceHttpResult" /> instance.</returns>
    public static RazorSliceHttpResult CreateHttpResult(SliceFactory sliceFactory, int statusCode = StatusCodes.Status200OK)
    {
        var result = (RazorSliceHttpResult)sliceFactory();
        result.StatusCode = statusCode;
        return result;
    }

    /// <summary>
    /// Creates and returns a new instance of a <see cref="RazorSliceHttpResult{TModel}" /> based on the provided name.
    /// </summary>
    /// <param name="sliceFactory">The <see cref="SliceFactory" /> delegate.</param>
    /// <param name="model">The model to use for the template instance.</param>
    /// <param name="statusCode">The HTTP status code to return. Defaults to <see cref="StatusCodes.Status200OK"/>.</param>
    /// <typeparam name="TModel">The model type of the template.</typeparam>
    /// <returns>The <see cref="RazorSliceHttpResult{TModel}" /> instance.</returns>
    public static RazorSliceHttpResult<TModel> CreateHttpResult<TModel>(SliceFactory<TModel> sliceFactory, TModel model, int statusCode = StatusCodes.Status200OK)
    {
        var result = (RazorSliceHttpResult<TModel>)RazorSliceFactory.Create(sliceFactory, model);
        result.StatusCode = statusCode;
        return result;
    }
}
