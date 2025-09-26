// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Microsoft.Rest;

namespace HttpRetries;

public static class RetryUtilities
{
    private static readonly SocketError[] SocketErrorCodes =
    [
        SocketError.ConnectionRefused,
        SocketError.TimedOut,
        SocketError.ConnectionReset
    ];

    private static readonly WebExceptionStatus[] WebExceptionStatus =
    [
        System.Net.WebExceptionStatus.ConnectionClosed,
        System.Net.WebExceptionStatus.Timeout,
        System.Net.WebExceptionStatus.RequestCanceled,
        System.Net.WebExceptionStatus.KeepAliveFailure,
        System.Net.WebExceptionStatus.PipelineFailure,
        System.Net.WebExceptionStatus.ReceiveFailure,
        System.Net.WebExceptionStatus.ConnectFailure,
        System.Net.WebExceptionStatus.SendFailure,
        System.Net.WebExceptionStatus.NameResolutionFailure
    ];

    public static bool IsRetryableException(this Exception e)
    {
        if (IsTaskCancelledException(e))
        {
            return true;
        }

        if (e is HttpRequestException re)
        {
            if (IsRetryableHttpStatusCode(re.StatusCode))
            {
                return true;
            }

            // Retry for below issue:
            // System.Net.Http.HttpRequestException: The SSL connection could not be established,
            // see inner exception.  --->System.IO.IOException: Received an unexpected EOF or 0
            // bytes from the transport stream.at
            // System.Net.Security.SslStream.ReceiveHandshakeFrameAsync[TIOAdapter](CancellationToken
            // cancellationToken)    at System.Net.Security.SslStream.ForceAuthenticationAsync
            // [TIOAdapter](Boolean receiveFirst, Byte[] reAuthenticationData, CancellationToken
            // cancellationToken)    at
            // System.Net.Http.ConnectHelper.EstablishSslConnectionAsync(
            // SslClientAuthenticationOptions sslOptions, HttpRequestMessage request,
            // Boolean async, Stream stream, CancellationToken cancellationToken)-- -
            // End of inner exception stack trace-- -
            // at System.Net.Http.ConnectHelper.EstablishSslConnectionAsync(
            // SslClientAuthenticationOptions sslOptions, HttpRequestMessage request,
            // Boolean async, Stream stream, CancellationToken cancellationToken)
            // at System.Net.Http.HttpConnectionPool.ConnectAsync(HttpRequestMessage request,
            // Boolean async, CancellationToken cancellationToken)
            if (re.InnerException is IOException ioe &&
                ioe.Message.Contains("Received an unexpected EOF or 0 bytes from the " +
                "transport stream"))
            {
                return true;
            }
        }

        if (e is HttpOperationException he && he.Response != null)
        {
            if (IsRetryableHttpStatusCode(he.Response.StatusCode))
            {
                return true;
            }
        }

        var socketException = FindFirstExceptionOfType<SocketException>(e);
        if (socketException != null &&
            SocketErrorCodes.Contains(socketException.SocketErrorCode))
        {
            return true;
        }

        // Seen Name or service not known error due to DNS resolution failures.
        if (socketException != null &&
            socketException.Message.StartsWith("Name or service not known"))
        {
            return true;
        }

        var webException = FindFirstExceptionOfType<WebException>(e);
        if (webException != null)
        {
            if (WebExceptionStatus.Contains(webException.Status))
            {
                return true;
            }

            if (webException.Status == System.Net.WebExceptionStatus.ProtocolError)
            {
                if (webException.Response is HttpWebResponse response &&
                    IsRetryableHttpStatusCode(response.StatusCode))
                {
                    return true;
                }
            }
        }

        return false;

        static bool IsRetryableHttpStatusCode(HttpStatusCode? code)
        {
            switch (code)
            {
                case HttpStatusCode.InternalServerError:
                case HttpStatusCode.ServiceUnavailable:
                case HttpStatusCode.RequestTimeout:
                case HttpStatusCode.GatewayTimeout:
                case HttpStatusCode.BadGateway:
                    return true;
            }

            return false;
        }
    }

    public static bool FindFirstExceptionOfType<T>(
        Exception e,
        [NotNullWhen(true)] out T? typedException)
        where T : Exception
    {
        typedException = FindFirstExceptionOfType<T>(e);
        return typedException != null;
    }

    public static T? FindFirstExceptionOfType<T>(this Exception e)
        where T : Exception
    {
        if (e == null)
        {
            return null;
        }

        Stack<Exception> stack = new();
        stack.Push(e);

        while (stack.Count != 0)
        {
            var ex = stack.Pop();

            if (ex is T retval)
            {
                return retval;
            }

            if (ex.InnerException != null)
            {
                stack.Push(ex.InnerException);
            }
        }

        return null;
    }

    private static bool IsTaskCancelledException(Exception e)
    {
        if (e is AggregateException ae)
        {
            foreach (var ex in ae.InnerExceptions)
            {
                if (!IsTaskCancelledException(ex))
                {
                    return false;
                }
            }

            return true;
        }
        else if (e is TaskCanceledException || e is OperationCanceledException)
        {
            return true;
        }

        return false;
    }
}
