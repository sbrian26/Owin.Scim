﻿namespace Owin.Scim.Patching.Helpers
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    using Antlr;

    using Antlr4.Runtime;

    using Configuration;

    using Exceptions;

    using Extensions;

    using Model;

    using NContext.Common;

    using Newtonsoft.Json.Serialization;

    using Operations;

    using Querying;

    // This code is based on ideas from Microsoft's (Microsoft.AspNet.JsonPatch) ObjectTreeAnalysisResult.cs
    // Pretty much all the original code is gone, however, and this has been heavily modified to add support 
    // for IEnumerable, SCIM query filters, and observe the rules surrounding SCIM Patch.
    public class ScimPatchObjectAnalysis
    {
        private readonly ScimServerConfiguration _ServerConfiguration;

        private readonly IContractResolver _ContractResolver;

        private readonly Operation _Operation;

        public ScimPatchObjectAnalysis(
            ScimServerConfiguration serverConfiguration,
            object objectToSearch, 
            string filter, 
            IContractResolver contractResolver,
            Operation operation)
        {
            _ServerConfiguration = serverConfiguration;
            _ContractResolver = contractResolver;
            _Operation = operation;
            PatchMembers = new List<PatchMember>();

            /* 
                ScimFilter.cs will handle normalizing the actual path string. 
                
                Examples:
                "path":"members"
                "path":"name.familyName"
                "path":"addresses[type eq \"work\"]"
                "path":"members[value eq \"2819c223-7f76-453a-919d-413861904646\"]"
                "path":"members[value eq \"2819c223-7f76-453a-919d-413861904646\"].displayName"

                Once normalized, associate each resource member with its filter (if present).
                This is represented as a PathMember, which is essentially a tuple of <memberName, memberFilter?>
            */
            var pathTree = new ScimFilter(_ServerConfiguration.ResourceExtensionSchemas.Keys, filter).Paths.ToList();
            var lastPosition = 0;
            var nodes = GetAffectedMembers(pathTree, ref lastPosition, new Node(objectToSearch, null));
            
            if ((pathTree.Count - lastPosition) > 1)
            {
                IsValidPathForAdd = false;
                IsValidPathForRemove = false;
                return;
            }

            foreach (var node in nodes)
            {
                var attribute = node.Target as MultiValuedAttribute;
                JsonProperty attemptedProperty;
                if (attribute != null && PathIsMultiValuedEnumerable(pathTree[pathTree.Count - 1].Path, node, out attemptedProperty))
                {
                    /* Check if we're at a MultiValuedAttribute.
                       If so, then we'll return a special PatchMember.  This is because our actual target is 
                       an element within an enumerable. (e.g. User->Emails[element])
                       So a PatchMember must have three pieces of information: (following the example above)
                       > Parent (User)
                       > PropertyPath (emails)
                       > Actual Target (email instance/element)
                    */

                    UseDynamicLogic = false;
                    IsValidPathForAdd = true;
                    IsValidPathForRemove = true;
                    PatchMembers.Add(
                        new PatchMember(
                            pathTree[pathTree.Count - 1].Path,
                            new JsonPatchProperty(attemptedProperty, node.Parent),
                            node.Target));
                }
                else
                {
                    UseDynamicLogic = false;

                    var jsonContract = (JsonObjectContract)contractResolver.ResolveContract(node.Target.GetType());
                    attemptedProperty = jsonContract.Properties.GetClosestMatchProperty(pathTree[pathTree.Count - 1].Path);
                    if (attemptedProperty == null)
                    {
                        IsValidPathForAdd = false;
                        IsValidPathForRemove = false;
                    }
                    else
                    {
                        IsValidPathForAdd = true;
                        IsValidPathForRemove = true;
                        PatchMembers.Add(
                            new PatchMember(
                                pathTree[pathTree.Count - 1].Path,
                                new JsonPatchProperty(attemptedProperty, node.Target)));
                    }
                }
            }
        }

        private bool PathIsMultiValuedEnumerable(string propertyName, Node node, out JsonProperty attemptedProperty)
        {
            var jsonContract = (JsonObjectContract)_ContractResolver.ResolveContract(node.Parent.GetType());
            attemptedProperty = jsonContract.Properties.GetClosestMatchProperty(propertyName);

            if (attemptedProperty != null &&
                attemptedProperty.PropertyType.IsNonStringEnumerable() &&
                attemptedProperty.PropertyType.IsGenericType &&
                attemptedProperty.PropertyType.GetGenericArguments()[0] == node.Target.GetType())
            {
                return true;
            }

            return false;
        }

        private IEnumerable<Node> GetAffectedMembers(
            IList<PathFilterExpression> pathTree, 
            ref int lastPosition,
            Node node)
        {
            for (int i = lastPosition; i < pathTree.Count; i++)
            {
                // seems absurd, but this MAY be called recursively, OR simply
                // iterated via the for loop
                lastPosition = i; 
                
                var jsonContract = (JsonObjectContract)_ContractResolver.ResolveContract(node.Target.GetType());
                var attemptedProperty = jsonContract.Properties.GetClosestMatchProperty(pathTree[i].Path);
                if (attemptedProperty == null)
                {
                    if (!_ServerConfiguration.ResourceExtensionExists(pathTree[i].Path))
                    {
                        // property cannot be found, and we're not working with an extension.
                        ErrorType = ScimErrorType.InvalidPath;
                        break;
                    }

                    // this is a resource extension
                    // TODO: (DG) the code below works as well and will remove once it's determined how 
                    // repositories will access and persist extension data.  Currently, Extensions property is public.
//                    var memberInfo = node.Target.GetType().GetProperty("Extensions", BindingFlags.NonPublic | BindingFlags.Instance);
//                    var property = new JsonProperty
//                    {
//                        PropertyType = memberInfo.PropertyType,
//                        DeclaringType = memberInfo.DeclaringType,
//                        ValueProvider = new ReflectionValueProvider(memberInfo),
//                        AttributeProvider = new ReflectionAttributeProvider(memberInfo),
//                        Readable = true,
//                        Writable = true,
//                        ShouldSerialize = member => false
//                    };

                    attemptedProperty = jsonContract.Properties.GetProperty("extensions", StringComparison.Ordinal);
                }

                // if there's nothing to filter and we're not yet at the last path entry, continue
                if (pathTree[i].Filter == null && i != pathTree.Count - 1)
                {
                    // if they enter an invalid target 
                    if (attemptedProperty.PropertyType.IsTerminalObject())
                    {
                        ErrorType = ScimErrorType.InvalidPath;
                        break;
                    }

                    object targetValue;
                    var propertyType = attemptedProperty.PropertyType;

                    // support for resource extensions
                    if (propertyType == typeof (ResourceExtensions))
                    {
                        var resourceExtensions = (ResourceExtensions) attemptedProperty.ValueProvider.GetValue(node.Target);
                        var extensionType = _ServerConfiguration.GetResourceExtensionType(node.Target.GetType(), pathTree[i].Path);
                        if (_Operation.OperationType == OperationType.Remove && !resourceExtensions.Contains(extensionType))
                            break;

                        targetValue = resourceExtensions.GetOrCreate(extensionType);
                    }
                    else
                    {
                        // if targetValue is null, then we need to initialize it, UNLESS we're in a remove operation
                        // e.g. user.name.givenName, when name == null
                        targetValue = attemptedProperty.ValueProvider.GetValue(node.Target);
                        if (targetValue == null)
                        {
                            if (_Operation.OperationType == OperationType.Remove)
                                break;

                            if (!propertyType.IsNonStringEnumerable())
                            {
                                // if just a normal complex type, just create a new instance
                                targetValue = propertyType.CreateInstance();
                            }
                            else
                            {
                                var enumerableInterface = propertyType.GetEnumerableType();
                                var listArgumentType = enumerableInterface.GetGenericArguments()[0];
                                var listType = typeof (List<>).MakeGenericType(listArgumentType);
                                targetValue = listType.CreateInstance();
                            }

                            attemptedProperty.ValueProvider.SetValue(node.Target, targetValue);
                        }
                    }

                    // the Target becomes the Target's child property value
                    // the Parent becomes the current Target
                    node = new Node(targetValue, node.Target);
                    continue; // keep traversing the path tree
                }
                    
                if (pathTree[i].Filter != null)
                {
                    // we can only filter enumerable types
                    if (!attemptedProperty.PropertyType.IsNonStringEnumerable())
                    {
                        ErrorType = ScimErrorType.InvalidFilter;
                        break;
                    }

                    var enumerable = attemptedProperty.ValueProvider.GetValue(node.Target) as IEnumerable;
                    if (enumerable == null)
                    {
                        // if the value of the attribute is null then there's nothing to filter
                        // it should never get here beause ScimObjectAdapter should apply a 
                        // different ruleset for null values; replacing or setting the attribute value
                        ErrorType = ScimErrorType.NoTarget;
                        break;
                    }
                    
                    dynamic predicate;
                    try
                    {
                        // parse our filter into an expression tree
                        var lexer = new ScimFilterLexer(new AntlrInputStream(pathTree[i].Filter));
                        var parser = new ScimFilterParser(new CommonTokenStream(lexer));

                        // create a visitor for the type of enumerable generic argument
                        var enumerableType = attemptedProperty.PropertyType.GetGenericArguments()[0];
                        var filterVisitorType = typeof (ScimFilterVisitor<>).MakeGenericType(enumerableType);
                        var filterVisitor = (IScimFilterVisitor) filterVisitorType.CreateInstance();
                        predicate = filterVisitor.VisitExpression(parser.parse()).Compile();
                    }
                    catch (Exception)
                    {
                        ErrorType = ScimErrorType.InvalidFilter;
                        break;
                    }

                    // we have an enumerable and a filter predicate
                    // for each element in the enumerable that satisfies the predicate, 
                    // visit that element as part of the path tree
                    var originalHasElements = false;
                    var filteredNodes = new List<Node>();
                    var enumerator = enumerable.GetEnumerator();
                    lastPosition = i + 1; // increase the position in the tree
                    while (enumerator.MoveNext())
                    {
                        originalHasElements = true;
                        dynamic currentElement = enumerator.Current;
                        if ((bool) predicate(currentElement))
                        {
                            filteredNodes.AddRange(
                                GetAffectedMembers(
                                    pathTree,
                                    ref lastPosition,
                                    new Node(enumerator.Current, node.Target)));
                        }
                    }

                    /* SCIM PATCH 'replace' RULE:
                        o  If the target location is a multi-valued attribute for which a
                            value selection filter ("valuePath") has been supplied and no
                            record match was made, the service provider SHALL indicate failure
                            by returning HTTP status code 400 and a "scimType" error code of
                            "noTarget".
                    */
                    if (originalHasElements &&
                        filteredNodes.Count == 0 &&
                        _Operation != null &&
                        _Operation.OperationType == OperationType.Replace)
                    {
                        throw new ScimPatchException(
                            ScimErrorType.NoTarget, _Operation);
                    }

                    return filteredNodes;
                }
            }

            return new List<Node> { node };
        }

        public bool UseDynamicLogic { get; private set; }

        public bool IsValidPathForAdd { get; private set; }

        public bool IsValidPathForRemove { get; private set; }

        public IDictionary<string, object> Container { get; private set; }

        public IList<PatchMember> PatchMembers { get; private set; } 

        public ScimErrorType ErrorType { get; set; }

        private class Node
        {
            public Node(object target, object parent)
            {
                Target = target;
                Parent = parent;
            }

            public object Target { get; private set; }

            public object Parent { get; private set; }
        }
    }
}