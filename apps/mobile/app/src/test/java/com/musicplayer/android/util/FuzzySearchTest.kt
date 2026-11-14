package com.musicplayer.android.util

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FuzzySearchTest {

    @Test
    fun `matches exact and partial queries`() {
        assertTrue(FuzzySearch.matches("Bohemian Rhapsody Queen", "bohemian"))
        assertTrue(FuzzySearch.matches("Bohemian Rhapsody Queen", "Queen Rhapsody"))
        assertTrue(FuzzySearch.matches("Bohemian Rhapsody Queen", "Bohem"))
        assertTrue(FuzzySearch.matches("Anything", ""))
    }

    @Test
    fun `tolerates typos`() {
        listOf(
            "Bohemain",
            "Bohemain Rapsody",
            "Rhapsodyy",
            "Rhaposdy",
            "Rahpsody",
        ).forEach { query ->
            assertTrue("expected '$query' to match", FuzzySearch.matches("Bohemian Rhapsody", query))
        }
        assertTrue(FuzzySearch.matches("The Beatles", "Beetles"))
        assertTrue(FuzzySearch.matches("Nothing Else Matters", "Nothnig Else Maters"))
    }

    @Test
    fun `rejects non-matches`() {
        assertFalse(FuzzySearch.matches("Bohemian Rhapsody", "Stairway to Heaven"))
        assertFalse(FuzzySearch.matches("Bohemian Rhapsody", "Bhmn"))
        assertFalse(FuzzySearch.matches("Bohemian Rhapsody", "xyz"))
        assertFalse(FuzzySearch.matches("Bohemian Rhapsody", "Bohemian Jazz"))
        assertFalse(FuzzySearch.matches("", "query"))
    }

    @Test
    fun `ignores diacritics`() {
        assertTrue(FuzzySearch.matches("Beyoncé", "beyonce"))
    }

    @Test
    fun `matches tokens across fields`() {
        assertTrue(
            FuzzySearch.matchesFields(
                listOf("Bohemian Rhapsody", "Queen", "A Night at the Opera"),
                "quuen opera"
            )
        )
        assertTrue(FuzzySearch.matchesFields(listOf("Song"), "   "))
    }
}
