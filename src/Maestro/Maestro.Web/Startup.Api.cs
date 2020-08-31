// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.ApiPagination;
using Microsoft.AspNetCore.ApiVersioning;
using Microsoft.AspNetCore.ApiVersioning.Swashbuckle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Newtonsoft.Json.Linq;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Maestro.Web
{
    public partial class Startup
    {
        private void ConfigureApiServices(IServiceCollection services)
        {
            services.AddApiVersioning(options => options.VersionByQuery("api-version"));
            services.AddSwaggerApiVersioning(
                (version, info) =>
                {
                    info.Description =
                        "The Web API enabling access to the Maestro++ service that supports the [.NET Core Dependency Flow infrastructure](https://github.com/dotnet/arcade/blob/master/Documentation/DependenciesFlowPlan.md).";
                    info.Contact = new OpenApiContact
                    {
                        Name = ".NET Core Engineering",
                        Email = "dnceng@microsoft.com",
                    };
                });

            services.AddSwaggerGen(
                options =>
                {
                    // If you get an exception saying 'Identical schemaIds detected for types Maestro.Web.Api.<version>.Models.<something> and Maestro.Web.Api.<different-version>.Models.<something>'
                    // Then you should NEVER add the following like the StackOverflow answers will suggest.
                    // options.CustomSchemaIds(x => x.FullName);

                    // This exception means that you have added something to one of the versions of the api that results in a conflict, because one version of the api cannot have 2 models for the same object
                    // e.g. If you add a new api, or just a new model to an existing version (with new or modified properties), every method in the API that can return this type,
                    // even nested (e.g. you changed Build, and Subscription contains a Build object), must be updated to return the new type.
                    // It could also mean that you forgot to apply [ApiRemoved] to an inherited method that shouldn't be included in the new version

                    options.FilterOperations(
                        (op, ctx) =>
                        {
                            var errorSchema = ctx.SchemaGenerator.GenerateSchema(typeof(ApiError), ctx.SchemaRepository);
                            op.Responses["default"] = new OpenApiResponse
                            {
                                Description = "Error",
                                Content = new Dictionary<string, OpenApiMediaType>
                                {
                                    ["application/json"] = new OpenApiMediaType
                                    {
                                        Schema = errorSchema,
                                    },
                                },
                            };
                            // Replace the large list generated by WepApi with just application/json 
                            // We can accept more than just application/json, but the swagger spec defines what we prefer
                            op.OperationId = $"{op.Tags.First().Name}_{op.OperationId}";
                        });

                    options.FilterOperations(
                        (op, ctx) =>
                        {
                            var paginated = ctx.MethodInfo.GetCustomAttribute<PaginatedAttribute>();
                            if (paginated != null)
                            {
                                // Add an extension that tells the client generator that this operation is paged with first,prev,next,last urls in the Link header.
                                op.Extensions["x-ms-paginated"] = new PaginatedExtension
                                {
                                    PageParameterName = paginated.PageParameterName,
                                    PageSizeParameterName = paginated.PageSizeParameterName
                                };
                            }
                        });

                    options.RequestBodyFilter<NameRequestBodyFilter>();

                    options.FilterSchemas(
                        (schema, ctx) =>
                        {
                            // Types that are not-nullable in C# should be required
                            if (schema.Type == "object")
                            {
                                var required = schema.Required == null
                                    ? new HashSet<string>()
                                    : new HashSet<string>(schema.Required.Select(ToCamelCase));
                                schema.Properties =
                                    schema.Properties.ToDictionary(
                                        p => ToCamelCase(p.Key),
                                        p => p.Value);
                                foreach (var property in schema.Properties.Keys)
                                {
                                    var propertyInfo = ctx.Type.GetRuntimeProperties().FirstOrDefault(p =>
                                        string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));
                                    if (propertyInfo != null)
                                    {
                                        var propertyType = propertyInfo.PropertyType;
                                        var shouldBeRequired =
                                            propertyType.IsValueType &&
                                            !(propertyType.IsConstructedGenericType &&
                                              propertyType.GetGenericTypeDefinition() == typeof(Nullable<>));
                                        if (shouldBeRequired)
                                        {
                                            required.Add(property);
                                        }
                                    }
                                }

                                schema.Required = required;
                            }
                        });

                    options.MapType<TimeSpan>(
                        () => new OpenApiSchema
                        {
                            Type = "string",
                            Format = "duration"
                        });
                    options.MapType<TimeSpan?>(
                        () => new OpenApiSchema
                        {
                            Type = "string",
                            Format = "duration"
                        });
                    options.MapType<JToken>(() => new OpenApiSchema());

                    options.DescribeAllParametersInCamelCase();

                    string xmlPath;
                    if (HostingEnvironment.IsDevelopment())
                    {
                        xmlPath = Path.GetDirectoryName(typeof(Startup).Assembly.Location);
                    }
                    else
                    {
                        xmlPath = HostingEnvironment.ContentRootPath;
                    }

                    string xmlFile = Path.Combine(xmlPath, "Maestro.Web.xml");
                    if (File.Exists(xmlFile))
                    {
                        options.IncludeXmlComments(xmlFile);
                    }

                    options.AddSecurityDefinition(
                        "Bearer",
                        new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.ApiKey,
                            In = ParameterLocation.Header,
                            Scheme = "bearer",
                            Name = HeaderNames.Authorization
                        });

                    options.AddSecurityRequirement(new OpenApiSecurityRequirement
                    {
                        {new OpenApiSecurityScheme{Reference = new OpenApiReference{Id = "Bearer", Type = ReferenceType.SecurityScheme}}, Array.Empty<string>()},
                    });
                });
            services.AddSwaggerGenNewtonsoftSupport();
        }

        private static string ToCamelCase(string value)
        {
            return value.Substring(0, 1).ToLowerInvariant() + value.Substring(1);
        }
    }

    internal class NameRequestBodyFilter : IRequestBodyFilter
    {
        public void Apply(OpenApiRequestBody requestBody, RequestBodyFilterContext context)
        {
            requestBody.Extensions["x-name"] = new RequestBodyNameExtension
            {
                Name = context.BodyParameterDescription.Name,
            };
        }
    }

    internal class RequestBodyNameExtension : IOpenApiExtension
    {
        public string Name { get; set; }

        public void Write(IOpenApiWriter writer, OpenApiSpecVersion specVersion)
        {
            writer.WriteValue(Name);
        }
    }

    internal class PaginatedExtension : IOpenApiExtension
    {
        public string PageParameterName { get; set; }
        public string PageSizeParameterName { get; set; }

        public void Write(IOpenApiWriter writer, OpenApiSpecVersion specVersion)
        {
            writer.WriteStartObject();
            writer.WriteProperty("page", PageParameterName);
            writer.WriteProperty("pageSize", PageSizeParameterName);
            writer.WriteEndObject();
        }
    }
}
