﻿namespace Owin.Scim.Configuration
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.ComponentModel.Composition;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Web.Http;
    using System.Web.Http.Controllers;
    using System.Web.Http.ExceptionHandling;
    using System.Web.Http.Dispatcher;
    using DryIoc;
    using DryIoc.WebApi;

    using Endpoints;

    using Extensions;

    using Middleware;

    using Model;

    using NContext.Common;
    using NContext.Configuration;
    using NContext.Extensions;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    using Querying;

    using Security;

    using Serialization;

    using Services;

    public class ScimApplicationManager : IApplicationComponent
    {
        private const string BuiltInAssemblyNamePrefix = @"Owin.Scim";

        private readonly IAppBuilder _AppBuilder;

        private readonly IList<Predicate<FileInfo>> _CompositionConstraints;

        private readonly ConcurrentDictionary<Type, bool> _TypeResolverCache;

        private readonly Action<ScimServerConfiguration> _ConfigureScimServerAction;
        
        private bool _IsConfigured;

        public ScimApplicationManager(
            IAppBuilder appBuilder, 
            IList<Predicate<FileInfo>> compositionConstraints, 
            Action<ScimServerConfiguration> configureScimServerAction)
        {
            _AppBuilder = appBuilder;
            _CompositionConstraints = compositionConstraints;
            _ConfigureScimServerAction = configureScimServerAction;
            _TypeResolverCache = new ConcurrentDictionary<Type, bool>();
        }

        public IContainer Container { get; private set; }

        public bool IsConfigured
        {
            get { return _IsConfigured; }
            private set { _IsConfigured = value; }
        }

        public IAppBuilder AppBuilder
        {
            get { return _AppBuilder; }
        }

        public void Configure(ApplicationConfigurationBase applicationConfiguration)
        {
            if (IsConfigured)
                return;

            var serverConfiguration = new ScimServerConfiguration();
            applicationConfiguration.CompositionContainer.ComposeExportedValue(serverConfiguration);
            
            // configure appBuilder middleware
            AppBuilder.Use<ExceptionHandlerMiddleware>(); // global exception handler for returning SCIM-formatted messages
            AppBuilder.Use((context, task) =>
            {
                AmbientRequestService.SetRequestInformation(context, serverConfiguration);

                return task.Invoke();
            });

            // discover and register all type definitions
            var versionedSchemaTypes = new Dictionary<ScimVersion, IList<Type>>
            {
                { ScimVersion.One, new List<Type>() },
                { ScimVersion.Two, new List<Type>() }
            };

            var typeDefinitions = applicationConfiguration.CompositionContainer.GetExportTypesThatImplement<IScimTypeDefinition>();
            foreach (var typeDefinition in typeDefinitions)
            {
                Type distinctTypeDefinition;
                var typeDefinitionTarget = GetTargetDefinitionType(typeDefinition); // the type of object being defined (e.g. User, Group, Name)
                if (serverConfiguration.TypeDefinitionRegistry.TryGetValue(typeDefinitionTarget, out distinctTypeDefinition))
                {
                    // already have a definition registered for the target type
                    // let's favor non-Owin.Scim definitions over built-in defaults
                    if (distinctTypeDefinition.Assembly.FullName.StartsWith(BuiltInAssemblyNamePrefix) &&
                        !typeDefinition.Assembly.FullName.StartsWith(BuiltInAssemblyNamePrefix))
                    {
                        serverConfiguration.TypeDefinitionRegistry[typeDefinitionTarget] = typeDefinition;
                    }

                    continue;
                }

                // register type definition
                if (typeof(IScimSchemaTypeDefinition).IsAssignableFrom(typeDefinition))
                {
                    var targetVersion = typeDefinitionTarget.Namespace.GetScimVersion() ?? serverConfiguration.DefaultScimVersion;
                    versionedSchemaTypes[targetVersion].Add(typeDefinitionTarget);
                }

                serverConfiguration.TypeDefinitionRegistry[typeDefinitionTarget] = typeDefinition;
            }

            serverConfiguration.SchemaTypeVersionCache = versionedSchemaTypes;

            // instantiate an instance of each type definition and add it to configuration
            // type definitions MAY instantiate new type definitions themselves during attribute definition composition
            var enumerator = serverConfiguration.TypeDefinitionRegistry.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                // creating type definitions may be expensive due to reflection
                // when a type definition is instantiated, it may implicitly instantiate/register other type 
                // definitions for complex attributes, therefore, no need to re-create the same definition more than once
                if (serverConfiguration.ContainsTypeDefinition(enumerator.Current))
                    continue;

                var typeDefinition = (IScimTypeDefinition)enumerator.Current.CreateInstance(serverConfiguration);
                serverConfiguration.AddTypeDefiniton(typeDefinition);
            }

            // define a new HttpConfiguration
            var httpConfiguration = serverConfiguration.HttpConfiguration = new HttpConfiguration();

            // invoke custom configuration action if not null
            if (_ConfigureScimServerAction != null)
                _ConfigureScimServerAction.Invoke(serverConfiguration);

            // register any optional middleware
            if (serverConfiguration.RequireSsl)
                AppBuilder.Use<RequireSslMiddleware>();

            // create a DryIoC container with an optional fallback dependency resolver if one has been specified
            if (serverConfiguration.DependencyResolver != null)
            {
                Container = new Container(
                    rules =>
                        rules.WithoutThrowIfDependencyHasShorterReuseLifespan()
                            .WithUnknownServiceResolvers(request =>
                            {
                                var shouldResolve = _TypeResolverCache.GetOrAdd(
                                    request.ServiceType,
                                    type =>
                                        _CompositionConstraints.Any(
                                            constraint =>
                                                constraint(new FileInfo(type.Assembly.Location))));

                                if (!shouldResolve)
                                    return null;

                                return new DelegateFactory(
                                    resolver =>
                                        serverConfiguration.DependencyResolver.Resolve(request.ServiceType));
                            }),
                    new AsyncExecutionFlowScopeContext());
            }
            else
            {
                Container = new Container(
                    rules => rules.WithoutThrowIfDependencyHasShorterReuseLifespan(),
                    new AsyncExecutionFlowScopeContext());
            }

            // Configure http configuration for SCIM
            ConfigureHttpConfiguration(serverConfiguration);

            // Register our ScimServerConfiguration as a singleton
            Container.RegisterInstance(serverConfiguration, Reuse.Singleton);

            Container.WithWebApi(httpConfiguration);
            AppBuilder.UseWebApi(httpConfiguration);

            IsConfigured = true;
        }

        private Type GetTargetDefinitionType(Type typeDefinition)
        {
            Type genericTypeDefinition;
            var baseType = typeDefinition.BaseType;
            if (baseType == null)
                throw new Exception("Invalid type definition. Must inherit from either ScimResourceTypeDefinitionBuilder or ScimTypeDefinitionBuilder.");

            while (!baseType.IsGenericType ||
                (((genericTypeDefinition = baseType.GetGenericTypeDefinition()) != typeof(ScimResourceTypeDefinitionBuilder<>)) && 
                 genericTypeDefinition != typeof(ScimSchemaTypeDefinitionBuilder<>) &&
                 genericTypeDefinition != typeof(ScimTypeDefinitionBuilder<>)))
            {
                if (baseType.BaseType == null)
                    throw new Exception("Invalid type definition. Must inherit from either ScimResourceTypeDefinitionBuilder or ScimTypeDefinitionBuilder.");

                baseType = baseType.BaseType;
            }

            return baseType.GetGenericArguments()[0];
        }

        private void ConfigureHttpConfiguration(ScimServerConfiguration serverConfiguration)
        {
            var httpConfiguration = serverConfiguration.HttpConfiguration;
            httpConfiguration.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always;
            httpConfiguration.MapHttpAttributeRoutes();

            var settings = httpConfiguration.Formatters.JsonFormatter.SerializerSettings;
            settings.ContractResolver = new ScimContractResolver(serverConfiguration)
            {
                IgnoreSerializableAttribute = true,
                IgnoreSerializableInterface = true
            };
            settings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
            settings.Converters.Add(new StringEnumConverter());
            settings.Converters.Add(new ScimQueryOptionsConverter(serverConfiguration));
            settings.Converters.Add(new ResourceJsonConverter(serverConfiguration, JsonSerializer.Create(settings)));
            
            httpConfiguration.Filters.Add(new ScimAuthorizationAttribute(serverConfiguration));

            httpConfiguration.ParameterBindingRules.Insert(
                0,
                descriptor =>
                {
                    if (typeof(Resource).IsAssignableFrom(descriptor.ParameterType))
                        return new ResourceParameterBinding(serverConfiguration, descriptor);

                    return null;
                });
            httpConfiguration.ParameterBindingRules.Insert(
                1,
                descriptor =>
                {
                    if (typeof(ScimQueryOptions).IsAssignableFrom(descriptor.ParameterType))
                        return new ScimQueryOptionsParameterBinding(descriptor, serverConfiguration);

                    return null;
                });

            // refer to https://tools.ietf.org/html/rfc7644#section-3.1
            httpConfiguration.Formatters.JsonFormatter.SupportedMediaTypes.Add(new System.Net.Http.Headers.MediaTypeHeaderValue("application/scim+json"));

            // See if user already provided an exception handler
            if (!(httpConfiguration.Services.GetService(typeof (IExceptionHandler)) is OwinScimExceptionHandler))
            {
                httpConfiguration.Services.Replace(typeof(IExceptionHandler), new OwinScimExceptionHandler());
            }
            httpConfiguration.Services.Replace(typeof(IHttpControllerTypeResolver), new DefaultHttpControllerTypeResolver(IsControllerType));
            httpConfiguration.Services.Replace(typeof(IHttpControllerSelector), new ScimHttpControllerSelector(httpConfiguration));

            httpConfiguration.Filters.Add(new ModelBindingResponseAttribute());
        }

        private static bool IsControllerType(Type t)
        {
            return
                typeof(ScimControllerBase).IsAssignableFrom(t) &&
                t != null &&
                t.IsClass &&
                t.IsVisible &&
                !t.IsAbstract &&
                typeof(IHttpController).IsAssignableFrom(t) &&
                HasValidControllerName(t);
        }

        private static bool HasValidControllerName(Type controllerType)
        {
            string controllerSuffix = DefaultHttpControllerSelector.ControllerSuffix;
            return controllerType.Name.Length > controllerSuffix.Length &&
                controllerType.Name.EndsWith(controllerSuffix, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal class ScimHttpControllerSelector : DefaultHttpControllerSelector
    {
        private readonly HttpConfiguration _Configuration;

        public ScimHttpControllerSelector(HttpConfiguration configuration) 
            : base(configuration)
        {
            _Configuration = configuration;
        }

        public override HttpControllerDescriptor SelectController(HttpRequestMessage request)
        {
            return base.SelectController(request);
        }

        public override string GetControllerName(HttpRequestMessage request)
        {
            var name = base.GetControllerName(request);

            return name;
        }

        public override IDictionary<string, HttpControllerDescriptor> GetControllerMapping()
        {
            var dictionary = new Dictionary<string, HttpControllerDescriptor>(StringComparer.OrdinalIgnoreCase);
            var assembliesResolver = _Configuration.Services.GetAssembliesResolver();
            var controllerResolver = _Configuration.Services.GetHttpControllerTypeResolver();
            var controllerTypes = controllerResolver.GetControllerTypes(assembliesResolver);

            foreach (var controllerType in controllerTypes)
            {
                string version = controllerType.Namespace.GetScimVersion() ?? string.Empty;
                var controllerName = controllerType.Name;//.Remove(controllerType.Name.Length - ControllerSuffix.Length);
                var controllerKey = string.Format(CultureInfo.InvariantCulture, "{0}{1}", version, controllerName);
                if (!dictionary.Keys.Contains(controllerKey))
                {
                    dictionary[controllerKey] = new HttpControllerDescriptor(_Configuration, controllerType.Name, controllerType);
                }
            }

            return dictionary;
        }
    }
}