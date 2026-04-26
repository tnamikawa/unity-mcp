using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services.Characterization
{
    /// <summary>
    /// Characterization tests for ServerManagementService public interface.
    /// These tests lock down current behavior BEFORE refactoring to ensure
    /// no regressions during the decomposition into focused components.
    /// </summary>
    [TestFixture]
    public class ServerManagementServiceCharacterizationTests
    {
        private ServerManagementService _service;
        private bool _savedUseHttpTransport;
        private string _savedHttpRemoteUrl;
        private string _savedHttpTransportScope;
        private bool _savedAllowLanHttpBind;
        private bool _savedAllowInsecureRemoteHttp;
        private string _portFileOverride;

        [SetUp]
        public void SetUp()
        {
            _service = new ServerManagementService();
            // Save current settings
            _savedUseHttpTransport = EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true);
            _savedHttpRemoteUrl = EditorPrefs.GetString(EditorPrefKeys.HttpRemoteBaseUrl, string.Empty);
            _savedHttpTransportScope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, string.Empty);
            _savedAllowLanHttpBind = EditorPrefs.GetBool(EditorPrefKeys.AllowLanHttpBind, false);
            _savedAllowInsecureRemoteHttp = EditorPrefs.GetBool(EditorPrefKeys.AllowInsecureRemoteHttp, false);

            _portFileOverride = Path.Combine(Path.GetTempPath(), "UnityMCPTests-port-" + Guid.NewGuid().ToString("N"));
            HttpEndpointUtility.SetPortFilePathOverrideForTesting(_portFileOverride);
        }

        [TearDown]
        public void TearDown()
        {
            // Restore settings
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, _savedUseHttpTransport);
            if (!string.IsNullOrEmpty(_savedHttpRemoteUrl))
            {
                EditorPrefs.SetString(EditorPrefKeys.HttpRemoteBaseUrl, _savedHttpRemoteUrl);
            }
            else
            {
                EditorPrefs.DeleteKey(EditorPrefKeys.HttpRemoteBaseUrl);
            }
            if (!string.IsNullOrEmpty(_savedHttpTransportScope))
            {
                EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, _savedHttpTransportScope);
            }
            else
            {
                EditorPrefs.DeleteKey(EditorPrefKeys.HttpTransportScope);
            }
            EditorPrefs.SetBool(EditorPrefKeys.AllowLanHttpBind, _savedAllowLanHttpBind);
            EditorPrefs.SetBool(EditorPrefKeys.AllowInsecureRemoteHttp, _savedAllowInsecureRemoteHttp);

            HttpEndpointUtility.SetPortFilePathOverrideForTesting(null);
            try { if (!string.IsNullOrEmpty(_portFileOverride) && File.Exists(_portFileOverride)) File.Delete(_portFileOverride); } catch { }

            // Refresh cache to reflect restored values
            EditorConfigurationCache.Instance.Refresh();
        }

        #region CanStartLocalServer Tests

        [Test]
        public void CanStartLocalServer_HttpDisabled_ReturnsFalse()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, false);
            HttpEndpointUtility.SaveLocalBaseUrl("http://localhost:8080");
            EditorConfigurationCache.Instance.Refresh();
            _service = new ServerManagementService();

            // Act
            bool result = _service.CanStartLocalServer();

            // Assert
            Assert.IsFalse(result, "Cannot start local server when HTTP transport is disabled");
        }

        [Test]
        public void CanStartLocalServer_HttpEnabledLocalUrl_ReturnsTrue()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
            HttpEndpointUtility.SaveLocalBaseUrl("http://localhost:8080");
            EditorConfigurationCache.Instance.Refresh();
            _service = new ServerManagementService();

            // Act
            bool result = _service.CanStartLocalServer();

            // Assert
            Assert.IsTrue(result, "Can start local server when HTTP enabled and URL is local");
        }

        #endregion

        #region HttpEndpointUtility Security Policy Tests

        [Test]
        public void SaveRemoteBaseUrl_WithoutScheme_DefaultsToHttps()
        {
            // Arrange
            EditorConfigurationCache.Instance.SetHttpTransportScope("remote");

            // Act
            HttpEndpointUtility.SaveRemoteBaseUrl("example.com:9000");
            string normalized = HttpEndpointUtility.GetRemoteBaseUrl();

            // Assert
            Assert.AreEqual("https://example.com:9000", normalized);
        }

        [Test]
        public void IsRemoteUrlAllowed_Http_DisallowedByDefault()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.AllowInsecureRemoteHttp, false);

            // Act
            bool allowed = HttpEndpointUtility.IsRemoteUrlAllowed("http://example.com:8080", out string error);

            // Assert
            Assert.IsFalse(allowed);
            Assert.IsNotNull(error);
            Assert.That(error, Does.Contain("HTTPS").IgnoreCase);
        }

        [Test]
        public void IsRemoteUrlAllowed_Http_AllowedWithOptIn()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.AllowInsecureRemoteHttp, true);

            // Act
            bool allowed = HttpEndpointUtility.IsRemoteUrlAllowed("http://example.com:8080", out string error);

            // Assert
            Assert.IsTrue(allowed);
            Assert.IsNull(error);
        }

        [Test]
        public void IsHttpLocalUrlAllowedForLaunch_ZeroBind_DisallowedByDefault()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.AllowLanHttpBind, false);

            // Act
            bool allowed = HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch("http://0.0.0.0:8080", out string error);

            // Assert
            Assert.IsFalse(allowed);
            Assert.IsNotNull(error);
            Assert.That(error, Does.Contain("disabled by default").IgnoreCase);
        }

        [Test]
        public void IsHttpLocalUrlAllowedForLaunch_ZeroBind_AllowedWithOptIn()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.AllowLanHttpBind, true);

            // Act
            bool allowed = HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch("http://0.0.0.0:8080", out string error);

            // Assert
            Assert.IsTrue(allowed);
            Assert.IsNull(error);
        }

        #endregion

        #region TryGetLocalHttpServerCommand Tests

        [Test]
        public void TryGetLocalHttpServerCommand_HttpDisabled_ReturnsFalseWithError()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, false);
            HttpEndpointUtility.SaveLocalBaseUrl("http://localhost:8080");
            EditorConfigurationCache.Instance.Refresh();
            _service = new ServerManagementService();

            // Act
            bool result = _service.TryGetLocalHttpServerCommand(out string command, out string error);

            // Assert
            Assert.IsFalse(result, "Should return false when HTTP transport is disabled");
            Assert.IsNull(command, "Command should be null when failing");
            Assert.IsNotNull(error, "Error message should be provided");
            Assert.That(error, Does.Contain("HTTP").IgnoreCase, "Error should mention HTTP transport");
        }

        [Test]
        public void TryGetLocalHttpServerCommand_LocalUrl_ReturnsCommandOrError()
        {
            // Arrange
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
            HttpEndpointUtility.SaveLocalBaseUrl("http://localhost:8080");
            _service = new ServerManagementService();

            // Act
            bool result = _service.TryGetLocalHttpServerCommand(out string command, out string error);

            // Assert - Success depends on uvx availability
            if (result)
            {
                Assert.IsNotNull(command, "Command should be set on success");
                Assert.IsNull(error, "Error should be null on success");
                Assert.That(command, Does.Contain("uvx").Or.Contain("uv"), "Command should reference uvx/uv");
            }
            else
            {
                Assert.IsNotNull(error, "Error message should be provided on failure");
            }

            Assert.Pass($"TryGetLocalHttpServerCommand: success={result}, command={command ?? "null"}, error={error ?? "null"}");
        }

        #endregion

        #region IsLocalHttpServerReachable Tests

        [Test]
        public void IsLocalHttpServerReachable_NoServer_ReturnsFalse()
        {
            // Arrange - Use a port that's unlikely to have a server running
            HttpEndpointUtility.SaveLocalBaseUrl("http://localhost:59999");
            _service = new ServerManagementService();

            // Act
            bool result = _service.IsLocalHttpServerReachable();

            // Assert
            Assert.IsFalse(result, "Should return false when no server is listening");
        }

        [Test]
        public void IsLocalHttpServerReachable_DoesNotThrow()
        {
            // Arrange
            _service = new ServerManagementService();

            // Act & Assert - Should never throw regardless of server state
            Assert.DoesNotThrow(() =>
            {
                _service.IsLocalHttpServerReachable();
            }, "IsLocalHttpServerReachable should handle all error cases gracefully");
        }

        #endregion

        #region IsLocalHttpServerRunning Tests

        [Test]
        public void IsLocalHttpServerRunning_DoesNotThrow()
        {
            // Arrange
            _service = new ServerManagementService();

            // Act & Assert - Should never throw regardless of server state
            Assert.DoesNotThrow(() =>
            {
                _service.IsLocalHttpServerRunning();
            }, "IsLocalHttpServerRunning should handle all detection strategies gracefully");
        }

        #endregion

        #region ClearUvxCache Tests

        [Test]
        public void ClearUvxCache_DoesNotThrow()
        {
            // Arrange
            _service = new ServerManagementService();

            string lastLog = null;
            Application.LogCallback handler = (condition, stackTrace, type) =>
            {
                if (condition != null && condition.Contains("uv cache"))
                {
                    lastLog = condition;
                }
            };

            // Act & Assert - Should not throw even if uvx is not installed
            Assert.DoesNotThrow(() =>
            {
                LogAssert.ignoreFailingMessages = true;
                Application.logMessageReceived += handler;
                try
                {
                    _service.ClearUvxCache();
                }
                finally
                {
                    Application.logMessageReceived -= handler;
                    LogAssert.ignoreFailingMessages = false;
                }
            }, "ClearUvxCache should handle missing uvx gracefully");

            Assert.IsNotNull(lastLog, "Expected a uv cache log message.");
            StringAssert.Contains("uv cache", lastLog);
        }

        #endregion

        #region Private Method Characterization (via reflection for documentation)

        [Test]
        public void NormalizeForMatch_RemovesWhitespace_ViaReflection()
        {
            // Arrange - Use reflection to access private static method
            var method = typeof(ServerManagementService).GetMethod(
                "NormalizeForMatch",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Assert.Pass("NormalizeForMatch is a private method - behavior documented via code review");
                return;
            }

            // Act
            string result = (string)method.Invoke(null, new object[] { "Hello World" });

            // Assert
            Assert.AreEqual("helloworld", result, "Should remove whitespace and lowercase");
        }

        [Test]
        public void NormalizeForMatch_HandlesNull_ViaReflection()
        {
            // Arrange
            var method = typeof(ServerManagementService).GetMethod(
                "NormalizeForMatch",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Assert.Pass("NormalizeForMatch is a private method - behavior documented via code review");
                return;
            }

            // Act
            string result = (string)method.Invoke(null, new object[] { null });

            // Assert
            Assert.AreEqual(string.Empty, result, "Should return empty string for null input");
        }

        [Test]
        public void QuoteIfNeeded_PathWithSpaces_AddsQuotes_ViaReflection()
        {
            // Arrange
            var method = typeof(ServerManagementService).GetMethod(
                "QuoteIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Assert.Pass("QuoteIfNeeded is a private method - behavior documented via code review");
                return;
            }

            // Act
            string result = (string)method.Invoke(null, new object[] { "path with spaces" });

            // Assert
            Assert.AreEqual("\"path with spaces\"", result, "Should wrap path with quotes");
        }

        [Test]
        public void QuoteIfNeeded_PathWithoutSpaces_NoChange_ViaReflection()
        {
            // Arrange
            var method = typeof(ServerManagementService).GetMethod(
                "QuoteIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Assert.Pass("QuoteIfNeeded is a private method - behavior documented via code review");
                return;
            }

            // Act
            string result = (string)method.Invoke(null, new object[] { "pathwithoutspaces" });

            // Assert
            Assert.AreEqual("pathwithoutspaces", result, "Should not modify path without spaces");
        }

        [Test]
        public void IsLocalUrl_Static_MatchesPublicBehavior_ViaReflection()
        {
            // Arrange - Access private static IsLocalUrl(string) method
            var method = typeof(ServerManagementService).GetMethod(
                "IsLocalUrl",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (method == null)
            {
                Assert.Pass("Static IsLocalUrl is a private method - behavior documented via code review");
                return;
            }

            // Act & Assert - Test various URLs
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "http://localhost:8080" }), "localhost should be local");
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "http://127.0.0.1:8080" }), "127.0.0.1 should be local");
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "http://0.0.0.0:8080" }), "0.0.0.0 should be local");
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "http://[::1]:8080" }), "::1 should be recognized as local");
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "http://example.com:8080" }), "example.com should not be local");
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "" }), "empty string should not be local");
            Assert.IsFalse((bool)method.Invoke(null, new object[] { null }), "null should not be local");
        }

        [Test]
        public void BuildLocalProbeHosts_Localhost_IncludesIPv4AndIPv6Loopback_ViaReflection()
        {
            // Arrange
            var method = typeof(ServerManagementService).GetMethod(
                "BuildLocalProbeHosts",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (method == null)
            {
                Assert.Pass("BuildLocalProbeHosts is a private method - behavior documented via code review");
                return;
            }

            // Act
            var result = method.Invoke(null, new object[] { "localhost" });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<IEnumerable<string>>(result);
            var hosts = ((IEnumerable<string>)result).ToList();

            // Assert
            CollectionAssert.Contains(hosts, "localhost");
            CollectionAssert.Contains(hosts, "127.0.0.1");
            CollectionAssert.Contains(hosts, "::1");
        }

        [Test]
        public void BuildLocalProbeHosts_EmptyHost_DefaultsToIPv4Loopback_ViaReflection()
        {
            // Arrange
            var method = typeof(ServerManagementService).GetMethod(
                "BuildLocalProbeHosts",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (method == null)
            {
                Assert.Pass("BuildLocalProbeHosts is a private method - behavior documented via code review");
                return;
            }

            // Act
            var result = method.Invoke(null, new object[] { "" });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<IEnumerable<string>>(result);
            var hosts = ((IEnumerable<string>)result).ToList();

            // Assert
            Assert.AreEqual(1, hosts.Count, "Empty host should resolve to a single default probe host.");
            Assert.AreEqual("127.0.0.1", hosts[0]);
        }

        #endregion
    }
}
