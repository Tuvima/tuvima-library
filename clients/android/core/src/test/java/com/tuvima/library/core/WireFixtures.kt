package com.tuvima.library.core

import java.io.File
import org.json.JSONObject

/** The shared API v1 wire fixtures (`tests/fixtures/native-client-v1`) that the Engine's contract tests also pin. */
internal object WireFixtures {
    /** Resolves Engine-relative paths the way the app does, against a fixed test address. */
    val absolute: (String?) -> String? = { path -> path?.let { "http://engine.test$it" } }

    fun text(name: String): String {
        var dir: File? = File("").absoluteFile
        while (dir != null) {
            val candidate = File(dir, "tests/fixtures/native-client-v1/$name")
            if (candidate.isFile) return candidate.readText()
            dir = dir.parentFile
        }
        error("tests/fixtures/native-client-v1/$name was not found above ${File("").absolutePath}")
    }

    fun json(name: String): JSONObject = JSONObject(text(name))

    fun universe(): UniverseDetail = parseUniverseDetail(json("universe.json"), absolute)
}
