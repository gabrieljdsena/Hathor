package com.musicplayer.android.data.remote

import android.util.Log
import com.musicplayer.android.BuildConfig
import java.sql.Connection
import java.sql.DriverManager
import java.util.Properties
import kotlinx.coroutines.delay

// Connection to the shared remote MySQL/TiDB (the same database the desktop
// app pushes to and the web app syncs with). Credentials come ONLY from
// local.properties via BuildConfig (DB_HOST/DB_PORT/DB_USER/DB_PASSWORD/
// DB_NAME — never hardcoded, never committed). Empty DB_HOST = sync
// disabled, exactly like desktop ("No remote DB configured").
//
// TLS mirrors the desktop fallback chain (CA file -> certifi -> unverified)
// in Connector/J terms: encrypt when the server offers it, never fail on
// certificates (useSSL=true, requireSSL=false, verifyServerCertificate=false,
// i.e. web SslMode=Preferred). Like web RemoteMySql: 60s connect timeout
// (serverless TiDB needs ~a minute to wake) + one retry.
object RemoteDb {
    private const val TAG = "RemoteDb"

    fun isConfigured(): Boolean = BuildConfig.DB_HOST.isNotBlank()

    fun endpoint(): String = "${BuildConfig.DB_HOST}:${dbPort()}"

    private fun dbPort(): Int = BuildConfig.DB_PORT.toIntOrNull() ?: 4000

    fun open(): Connection {
        Class.forName("com.mysql.jdbc.Driver")
        val props = Properties().apply {
            setProperty("user", BuildConfig.DB_USER)
            setProperty("password", BuildConfig.DB_PASSWORD)
            setProperty("useSSL", "true")
            setProperty("requireSSL", "false")
            setProperty("verifyServerCertificate", "false")
            setProperty("connectTimeout", "60000")
            setProperty("socketTimeout", "120000")
            setProperty("useUnicode", "true")
            setProperty("characterEncoding", "utf8")
        }
        val url =
            "jdbc:mysql://${BuildConfig.DB_HOST}:${dbPort()}/${BuildConfig.DB_NAME}"
        return try {
            DriverManager.getConnection(url, props)
        } catch (e: Exception) {
            // Waking serverless clusters: one retry before giving up (web parity).
            Log.w(TAG, "Remote DB connect failed (${endpoint()}); retrying once", e)
            Thread.sleep(5000)
            DriverManager.getConnection(url, props)
        }
    }

    /** Guarded-table probe (desktop try/except + web TryQueryAsync): old
     * remotes predate podcasts/tags/mix tables. */
    fun tableExists(conn: Connection, table: String): Boolean {
        conn.prepareStatement(
            "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = DATABASE() AND table_name = ?",
        ).use { stmt ->
            stmt.setString(1, table)
            stmt.executeQuery().use { rs -> return rs.next() && rs.getLong(1) > 0 }
        }
    }

    fun columnExists(conn: Connection, table: String, column: String): Boolean {
        conn.prepareStatement(
            "SELECT COUNT(*) FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = ? AND column_name = ?",
        ).use { stmt ->
            stmt.setString(1, table)
            stmt.setString(2, column)
            stmt.executeQuery().use { rs -> return rs.next() && rs.getLong(1) > 0 }
        }
    }
}
