package com.musicplayer.android.data.remote

import java.sql.Connection
import java.sql.DriverManager

/**
 * Shared MySQL/TiDB connector (desktop sync.py get_mysql_connection()).
 * TLS via the Android system CA store (covers TiDB Cloud); desktop used
 * certifi for the same purpose. DB_SSL_CA from the desktop .env is a
 * Windows-only path and intentionally not consumed on Android.
 */
internal object RemoteConn {
    fun connect(config: RemoteConfig): Connection {
        if (!config.isConfigured) {
            throw IllegalStateException("No remote DB configured. Remote sync unavailable.")
        }
        try {
            // 5.1.x driver class (8.x/9.x need java.sql.SQLType, absent on Android).
            Class.forName("com.mysql.jdbc.Driver")
        } catch (e: ClassNotFoundException) {
            throw IllegalStateException("MySQL driver missing: $e")
        }
        // 5.1.x property set only: useSSL/requireSSL/verifyServerCertificate +
        // timeouts. (No enabledTLSProtocols / characterEncoding — unknown to 5.1.)
        val url = "jdbc:mysql://${config.host}:${config.port}/${config.database}" +
            "?useSSL=true&requireSSL=true&verifyServerCertificate=true" +
            "&connectTimeout=15000&socketTimeout=60000"
        return DriverManager.getConnection(url, config.user, config.password)
    }
}
