// Generates SafeScript's PrimitiveSurface table (src/dotNet/SafeScript.cs, between the GENERATED markers): every member
// name `value.name` resolves to (typeof is not "undefined") on a JS string, number or boolean in the source runtime's
// Nashorn. Run with the JDK 8 the oracle pins (8u504): jjs nashorn-primitive-surface.js
//
// Dot access on a primitive finds its JS prototype chain first and then falls back to the members of the Java class the
// value is (java.lang.String, Integer / Long / Double, Boolean): methods and bean properties, so 'walk'.startsWith is
// a function and 'walk'.empty a boolean there, which ES5.1 alone would read as undefined. A value from a Java Map (as a
// VariableMap value reaches a script) resolves the same as a literal; the probe checks that too. Synthetic values only.
var values = { "string": "'walk'", "javaString": "fromJava", "int": "7", "long": "1099511627776", "double": "7.5",
    "nan": "NaN", "boolean": "true" };
var m = new java.util.HashMap();
m.put('k', 'walk');
var fromJava = m.get('k');

var candidates = Object.create(null);
function addChain(v) {
    var o = Object(v);
    while (o !== null) {
        Object.getOwnPropertyNames(o).forEach(function (n) { if (!/^[0-9]+$/.test(n)) candidates[n] = true; });
        o = Object.getPrototypeOf(o);
    }
}
['', 0, true, {}].forEach(addChain);
function decapitalize(n) {
    if (n.length > 1 && n.charAt(0) === n.charAt(0).toUpperCase() && n.charAt(1) === n.charAt(1).toUpperCase()) return n;
    return n.charAt(0).toLowerCase() + n.substring(1);
}
[java.lang.String, java.lang.Integer, java.lang.Long, java.lang.Double, java.lang.Number, java.lang.Boolean, java.lang.Object]
    .forEach(function (type) {
        var methods = type.class.getMethods();
        for (var i = 0; i < methods.length; i++) {
            var name = methods[i].getName();
            candidates[name] = true;
            var bean = /^(get|is)([A-Z].*)$/.exec(name);
            if (bean && methods[i].getParameterCount() === 0) candidates[decapitalize(bean[2])] = true;
        }
    });

function resolves(expr, name) {
    try { return eval("typeof (" + expr + ")." + name) !== "undefined"; } catch (e) { return true; }
}
var surface = [];
Object.keys(candidates).sort().forEach(function (name) {
    if (!/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name)) return;
    for (var kind in values) if (resolves(values[kind], name)) { surface.push(name); return; }
});
var javaAgrees = true;
Object.keys(candidates).forEach(function (name) {
    if (/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name) && resolves("'walk'", name) !== resolves("fromJava", name)) javaAgrees = false;
});
print("// java.version " + java.lang.System.getProperty("java.version") + "; Map-backed string resolves as a literal: " + javaAgrees);
print(surface.join(" "));
