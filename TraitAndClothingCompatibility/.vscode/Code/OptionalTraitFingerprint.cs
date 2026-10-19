using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TraitAndClothingCompatibility {
    // Numeric tokens retain Newtonsoft's comparison semantics; only authored string values are opaque.
    internal static class OptionalTraitFingerprint {
        internal const string StringPrefix = "TACC.String.SHA256.v1:";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static JToken Project(JToken value) {
            if (value == null) throw new ArgumentNullException(nameof(value));
            JToken result = value.DeepClone();
            if (result.Type == JTokenType.String) return new JValue(StringHash(result.Value<string>()));
            if (result is JContainer container) {
                foreach (JToken token in container.Descendants().Where(t => t.Type == JTokenType.String).ToArray())
                    token.Replace(new JValue(StringHash(token.Value<string>())));
            }
            return result;
        }

        private static string StringHash(string value) {
            using var sha = SHA256.Create();
            return StringPrefix + BitConverter.ToString(sha.ComputeHash(StrictUtf8.GetBytes(value))).Replace("-", "");
        }
    }
}
