//Copyright (c) Service Stack LLC. All Rights Reserved.
//License: https://raw.github.com/ServiceStack/ServiceStack/master/license.txt

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using ServiceStack.Logging;
using ServiceStack.Serialization;
using ServiceStack.Text;
using ServiceStack.Text.FastMember;
using ServiceStack.Web;

namespace ServiceStack.Host.Handlers
{
    public abstract class ServiceStackHandlerBase
        : HttpAsyncTaskHandler, IHttpAsyncHandler
    {
        internal static readonly ILog Log = LogManager.GetLogger(typeof(ServiceStackHandlerBase));
        internal static readonly Dictionary<byte[], byte[]> NetworkInterfaceIpv4Addresses = new Dictionary<byte[], byte[]>();
        internal static readonly byte[][] NetworkInterfaceIpv6Addresses = new byte[0][];

        static ServiceStackHandlerBase()
        {
            try
            {
                IPAddressExtensions.GetAllNetworkInterfaceIpv4Addresses().ForEach((x, y) => NetworkInterfaceIpv4Addresses[x.GetAddressBytes()] = y.GetAddressBytes());

                NetworkInterfaceIpv6Addresses = IPAddressExtensions.GetAllNetworkInterfaceIpv6Addresses().ConvertAll(x => x.GetAddressBytes()).ToArray();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to retrieve IP Addresses, some security restriction features may not work: " + ex.Message, ex);
            }
        }

        public RequestAttributes HandlerAttributes { get; set; }

        public override bool IsReusable
        {
            get { return false; }
        }

        public abstract object CreateRequest(IHttpRequest request, string operationName);
        public abstract object GetResponse(IHttpRequest httpReq, IHttpResponse httpRes, object request);

        public Task HandleResponse(object response, Func<object, Task> callback, Func<Exception, Task> errorCallback)
        {
            try
            {
                var taskResponse = response as Task;
                if (taskResponse != null)
                {
                    if (taskResponse.Status == TaskStatus.Created)
                    {
                        taskResponse.Start();
                    }

                    return taskResponse
                        .ContinueWith(task =>
                        {
                            if (task.IsCompleted)
                            {
                                var taskResult = task.GetResult();
                                return callback(taskResult);
                            }

                            if (task.IsFaulted)
                                return errorCallback(task.Exception);

                            return task.IsCanceled
                                ? errorCallback(new OperationCanceledException("The async Task operation was cancelled"))
                                : errorCallback(new InvalidOperationException("Unknown Task state"));
                        });
                }

                return callback(response);
            }
            catch (Exception ex)
            {
                return errorCallback(ex);
            }
        }

        public static object DeserializeHttpRequest(Type operationType, IHttpRequest httpReq, string contentType)
        {
            var httpMethod = httpReq.HttpMethod;
            var queryString = httpReq.QueryString;

            if (httpMethod == HttpMethods.Get || httpMethod == HttpMethods.Delete || httpMethod == HttpMethods.Options)
            {
                try
                {
                    return KeyValueDataContractDeserializer.Instance.Parse(queryString, operationType);
                }
                catch (Exception ex)
                {
                    var msg = "Could not deserialize '{0}' request using KeyValueDataContractDeserializer: '{1}'.\nError: '{2}'"
                        .Fmt(operationType, queryString, ex);
                    throw new SerializationException(msg);
                }
            }

            var isFormData = httpReq.HasAnyOfContentTypes(MimeTypes.FormUrlEncoded, MimeTypes.MultiPartFormData);
            if (isFormData)
            {
                try
                {
                    return KeyValueDataContractDeserializer.Instance.Parse(httpReq.FormData, operationType);
                }
                catch (Exception ex)
                {
                    throw new SerializationException("Error deserializing FormData: " + httpReq.FormData, ex);
                }
            }

            var request = CreateContentTypeRequest(httpReq, operationType, contentType);
            return request;
        }

        protected static object CreateContentTypeRequest(IHttpRequest httpReq, Type requestType, string contentType)
        {
            try
            {
                if (!string.IsNullOrEmpty(contentType) && httpReq.ContentLength > 0)
                {
                    var deserializer = HostContext.ContentTypes.GetStreamDeserializer(contentType);
                    if (deserializer != null)
                    {
                        return deserializer(requestType, httpReq.InputStream);
                    }
                }
            }
            catch (Exception ex)
            {
                var msg = "Could not deserialize '{0}' request using {1}'\nError: {2}"
                    .Fmt(contentType, requestType, ex);
                throw new SerializationException(msg);
            }
            return requestType.CreateInstance(); //Return an empty DTO, even for empty request bodies
        }

        protected static object GetCustomRequestFromBinder(IHttpRequest httpReq, Type requestType)
        {
            Func<IHttpRequest, object> requestFactoryFn;
            HostContext.ServiceController.RequestTypeFactoryMap.TryGetValue(
                requestType, out requestFactoryFn);

            return requestFactoryFn != null ? requestFactoryFn(httpReq) : null;
        }

        public static Type GetOperationType(string operationName)
        {
            return HostContext.Metadata.GetOperationType(operationName);
        }

        protected static object ExecuteService(object request, RequestAttributes requestAttributes,
            IHttpRequest httpReq, IHttpResponse httpRes)
        {
            return HostContext.ExecuteService(request, requestAttributes, httpReq, httpRes);
        }

        public RequestAttributes GetEndpointAttributes(System.ServiceModel.OperationContext operationContext)
        {
            if (!HostContext.Config.EnableAccessRestrictions) return default(RequestAttributes);

            var portRestrictions = default(RequestAttributes);
            var ipAddress = GetIpAddress(operationContext);

            portRestrictions |= HttpRequestExtensions.GetAttributes(ipAddress);

            //TODO: work out if the request was over a secure channel			
            //portRestrictions |= request.IsSecureConnection ? PortRestriction.Secure : PortRestriction.InSecure;

            return portRestrictions;
        }

        public static IPAddress GetIpAddress(System.ServiceModel.OperationContext context)
        {
#if !MONO
            var prop = context.IncomingMessageProperties;
            if (context.IncomingMessageProperties.ContainsKey(System.ServiceModel.Channels.RemoteEndpointMessageProperty.Name))
            {
                var endpoint = prop[System.ServiceModel.Channels.RemoteEndpointMessageProperty.Name]
                    as System.ServiceModel.Channels.RemoteEndpointMessageProperty;
                if (endpoint != null)
                {
                    return IPAddress.Parse(endpoint.Address);
                }
            }
#endif
            return null;
        }

        protected static void AssertOperationExists(string operationName, Type type)
        {
            if (type == null)
            {
                throw new NotImplementedException(
                    string.Format("The operation '{0}' does not exist for this service", operationName));
            }
        }

        protected Task HandleException(IHttpRequest httpReq, IHttpResponse httpRes, string operationName, Exception ex)
        {
            var errorMessage = string.Format("Error occured while Processing Request: {0}", ex.Message);
            Log.Error(errorMessage, ex);

            try
            {
                HostContext.RaiseUncaughtException(httpReq, httpRes, operationName, ex);
                return EmptyTask;
            }
            catch (Exception writeErrorEx)
            {
                //Exception in writing to response should not hide the original exception
                Log.Info("Failed to write error to response: {0}", writeErrorEx);
                //rethrow the original exception
                return ex.AsTaskException();
            }
            finally
            {
                httpRes.EndRequest(skipHeaders: true);
            }
        }

        protected bool AssertAccess(IHttpRequest httpReq, IHttpResponse httpRes, Feature feature, string operationName)
        {
            if (operationName == null)
                throw new ArgumentNullException("operationName");

            if (HostContext.Config.EnableFeatures != Feature.All)
            {
                if (!HostContext.HasFeature(feature))
                {
                    HostContext.AppHost.HandleErrorResponse(httpReq, httpRes, HttpStatusCode.Forbidden, "Feature Not Available");
                    return false;
                }
            }

            var format = feature.ToFormat();
            if (!HostContext.Metadata.CanAccess(httpReq, format, operationName))
            {
                HostContext.AppHost.HandleErrorResponse(httpReq, httpRes, HttpStatusCode.Forbidden, "Service Not Available");
                return false;
            }
            return true;
        }

        private static void WriteDebugRequest(IRequestContext requestContext, object dto, IHttpResponse httpRes)
        {
            var bytes = Encoding.UTF8.GetBytes(dto.SerializeAndFormat());
            httpRes.OutputStream.Write(bytes, 0, bytes.Length);
        }

        public Task WriteDebugResponse(IHttpResponse httpRes, object response)
        {
            return httpRes.WriteToResponse(response, WriteDebugRequest,
                new SerializationContext(MimeTypes.PlainText));
        }
    }
}