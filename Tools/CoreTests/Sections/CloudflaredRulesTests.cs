using System.Security.Cryptography;
using System.Text;
using TeamCreate;

// The rules around `cloudflared tunnel --url ...`: which text counts as the public address, what the editor passes to the process,
// and whether a downloaded executable is the one the code pinned. No process, network or file is touched here; the tests feed the
// rules the exact shapes cloudflared prints and the shapes an attacker (or a confused log line) could print instead.
public static class CloudflaredRulesTests
{
    public static void Run( Action<bool, string> assert )
    {
        ParseAcceptsRealShapes( assert );
        ParseRejectsEverythingElse( assert );
        ParseLineLengthBound( assert );
        RelayUrl( assert );
        Arguments( assert );
        DownloadCheck( assert );
    }

    private static void ParseAcceptsRealShapes( Action<bool, string> assert )
    {
        assert( CloudflaredRules.ParseTunnelAddress( "2026-10-02T10:00:00Z INF |  https://random-words-here.trycloudflare.com  |" ) == "https://random-words-here.trycloudflare.com",
            "cf_parse_realistic_banner_line" );
        assert( CloudflaredRules.ParseTunnelAddress( "2026-10-02T10:00:03Z INF |  https://random-words-here.trycloudflare.com                                               |" ) == "https://random-words-here.trycloudflare.com",
            "cf_parse_banner_line_padded_to_the_box_width" );
        assert( CloudflaredRules.ParseTunnelAddress( "||| |  https://abc-123.trycloudflare.com |||  | |" ) == "https://abc-123.trycloudflare.com",
            "cf_parse_extra_pipes_and_spaces" );
        assert( CloudflaredRules.ParseTunnelAddress( "https://abc-123.trycloudflare.com" ) == "https://abc-123.trycloudflare.com",
            "cf_parse_bare_address" );
        assert( CloudflaredRules.ParseTunnelAddress( "|\thttps://abc-123.trycloudflare.com\t|\r" ) == "https://abc-123.trycloudflare.com",
            "cf_parse_tabs_and_carriage_return_around_the_address" );
        assert( CloudflaredRules.ParseTunnelAddress( "|  HTTPS://Random-Words.TryCloudflare.COM  |" ) == "https://random-words.trycloudflare.com",
            "cf_parse_upper_case_is_normalised_to_lower_case" );
        assert( CloudflaredRules.ParseTunnelAddress( "|  https://first-one.trycloudflare.com  https://second-one.trycloudflare.com  |" ) == "https://first-one.trycloudflare.com",
            "cf_parse_first_match_wins" );
        assert( CloudflaredRules.ParseTunnelAddress( "https://api.trycloudflare.com/tunnel | https://real-one.trycloudflare.com |" ) == "https://real-one.trycloudflare.com",
            "cf_parse_skips_an_invalid_first_candidate_and_returns_the_first_valid_one" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://a.trycloudflare.com |" ) == "https://a.trycloudflare.com",
            "cf_parse_one_character_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://1234.trycloudflare.com |" ) == "https://1234.trycloudflare.com",
            "cf_parse_digit_only_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://a--b.trycloudflare.com |" ) == "https://a--b.trycloudflare.com",
            "cf_parse_inner_double_hyphen" );

        // Only the exact label `api` is reserved: a tunnel whose random name merely contains it is a real tunnel.
        assert( CloudflaredRules.ParseTunnelAddress( "| https://api-test.trycloudflare.com |" ) == "https://api-test.trycloudflare.com",
            "cf_parse_label_starting_with_api_is_not_reserved" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://myapi.trycloudflare.com |" ) == "https://myapi.trycloudflare.com",
            "cf_parse_label_ending_with_api_is_not_reserved" );

        string longestLabel = new string( 'a', 63 );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://" + longestLabel + ".trycloudflare.com |" ) == "https://" + longestLabel + ".trycloudflare.com",
            "cf_parse_63_character_label_is_the_longest_accepted" );
    }

    private static void ParseRejectsEverythingElse( Action<bool, string> assert )
    {
        // The reserved API host: cloudflared logs it before the tunnel exists, and a returned api address would be handed to friends.
        assert( CloudflaredRules.ParseTunnelAddress( "|  https://api.trycloudflare.com  |" ) == null, "cf_parse_rejects_api_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "INF Requesting new quick Tunnel on trycloudflare.com..." ) == null, "cf_parse_rejects_requesting_line" );
        assert( CloudflaredRules.ParseTunnelAddress( "https://api.trycloudflare.com/tunnel" ) == null, "cf_parse_rejects_api_url_with_path" );
        assert( CloudflaredRules.ParseTunnelAddress( "|  https://API.trycloudflare.com  |" ) == null, "cf_parse_rejects_api_label_in_upper_case" );
        assert( CloudflaredRules.ParseTunnelAddress( "ERR Error unmarshaling QuickTunnel response: error=\"Post \\\"https://api.trycloudflare.com/tunnel\\\": dial tcp: lookup api.trycloudflare.com: no such host\"" ) == null,
            "cf_parse_rejects_error_line_that_mentions_the_api_host" );

        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com/path |" ) == null, "cf_parse_rejects_path" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com/ |" ) == null, "cf_parse_rejects_root_path" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com?a=b |" ) == null, "cf_parse_rejects_query" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com#frag |" ) == null, "cf_parse_rejects_fragment" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com:8443 |" ) == null, "cf_parse_rejects_port" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://user@x.trycloudflare.com |" ) == null, "cf_parse_rejects_userinfo" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://user:secret@x.trycloudflare.com |" ) == null, "cf_parse_rejects_userinfo_with_password" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com@evil.com |" ) == null, "cf_parse_rejects_host_hidden_in_userinfo" );
        assert( CloudflaredRules.ParseTunnelAddress( "| http://x.trycloudflare.com |" ) == null, "cf_parse_rejects_http" );
        assert( CloudflaredRules.ParseTunnelAddress( "| wss://x.trycloudflare.com |" ) == null, "cf_parse_rejects_wss_scheme" );
        assert( CloudflaredRules.ParseTunnelAddress( "| x.trycloudflare.com |" ) == null, "cf_parse_rejects_missing_scheme" );

        assert( CloudflaredRules.ParseTunnelAddress( "| https://evil.com/.trycloudflare.com |" ) == null, "cf_parse_rejects_lookalike_suffix_after_path" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com.evil.com |" ) == null, "cf_parse_rejects_lookalike_trailing_domain" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com-evil.com |" ) == null, "cf_parse_rejects_lookalike_hyphen_extension" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.co |" ) == null, "cf_parse_rejects_lookalike_wrong_tld" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://trycloudflare.com |" ) == null, "cf_parse_rejects_apex_without_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://some-other-host.example.org |" ) == null, "cf_parse_rejects_a_long_host_that_is_not_under_trycloudflare_com" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://abcdefghijklmnopqrstuvwxyz.com |" ) == null, "cf_parse_rejects_a_long_host_whose_first_characters_would_make_a_valid_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://evil.com |" ) == null, "cf_parse_rejects_a_short_unrelated_host" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://.trycloudflare.com |" ) == null, "cf_parse_rejects_empty_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://xtrycloudflare.com |" ) == null, "cf_parse_rejects_suffix_without_the_dot" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.y.trycloudflare.com |" ) == null, "cf_parse_rejects_two_labels" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com. |" ) == null, "cf_parse_rejects_trailing_dot" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://evil.com/https://x.trycloudflare.com |" ) == null, "cf_parse_rejects_address_embedded_after_another_url" );
        assert( CloudflaredRules.ParseTunnelAddress( "| xhttps://x.trycloudflare.com |" ) == null, "cf_parse_rejects_scheme_glued_to_other_text" );
        assert( CloudflaredRules.ParseTunnelAddress( "| \"https://x.trycloudflare.com\" |" ) == null, "cf_parse_rejects_address_in_quotes" );

        assert( CloudflaredRules.ParseTunnelAddress( "| https://-x.trycloudflare.com |" ) == null, "cf_parse_rejects_label_starting_with_hyphen" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x-.trycloudflare.com |" ) == null, "cf_parse_rejects_label_ending_with_hyphen" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://-.trycloudflare.com |" ) == null, "cf_parse_rejects_label_that_is_one_hyphen" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x_y.trycloudflare.com |" ) == null, "cf_parse_rejects_underscore_in_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x\u00E9.trycloudflare.com |" ) == null, "cf_parse_rejects_non_ascii_letter_in_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x\u3002trycloudflare.com |" ) == null, "cf_parse_rejects_ideographic_full_stop_as_separator" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://" + new string( 'a', 64 ) + ".trycloudflare.com |" ) == null, "cf_parse_rejects_64_character_label" );
        assert( CloudflaredRules.ParseTunnelAddress( "| https://x.trycloudflare.com\0 |" ) == null, "cf_parse_rejects_control_character_after_host" );

        assert( CloudflaredRules.ParseTunnelAddress( null ) == null, "cf_parse_rejects_null" );
        assert( CloudflaredRules.ParseTunnelAddress( "" ) == null, "cf_parse_rejects_empty" );
        assert( CloudflaredRules.ParseTunnelAddress( "   |  |   " ) == null, "cf_parse_rejects_only_spaces_and_pipes" );
        assert( CloudflaredRules.ParseTunnelAddress( "+----------------------------------------------+" ) == null, "cf_parse_rejects_box_border_line" );
    }

    private static void ParseLineLengthBound( Action<bool, string> assert )
    {
        // 4,096 characters is the most one line may carry; the address sits at the start so only the length decides the outcome.
        string address = "https://x.trycloudflare.com";
        string atLimit = address + " " + new string( '|', 4096 - address.Length - 1 );
        string overLimit = atLimit + "|";
        assert( atLimit.Length == 4096 && overLimit.Length == 4097, "cf_parse_length_fixture_is_exact" );
        assert( CloudflaredRules.ParseTunnelAddress( atLimit ) == address, "cf_parse_accepts_a_4096_character_line" );
        assert( CloudflaredRules.ParseTunnelAddress( overLimit ) == null, "cf_parse_rejects_a_4097_character_line" );
    }

    private static void RelayUrl( Action<bool, string> assert )
    {
        string url = CloudflaredRules.ToRelayUrl( "https://random-words-here.trycloudflare.com" );
        assert( url == "wss://random-words-here.trycloudflare.com/relay", "cf_relay_url_of_valid_address" );
        assert( url != null && url.EndsWith( RelayProtocol.DefaultPath, StringComparison.Ordinal ), "cf_relay_url_uses_the_protocols_default_path" );

        // The result is what the engine will be asked to dial, so it has to satisfy the engine's own WebSocket policy (wss, DNS name, no IP).
        assert( RelayProtocol.TryValidateUrl( url, out Uri parsed, out string policyError ) && policyError == null && parsed.Scheme == "wss" && parsed.Host == "random-words-here.trycloudflare.com",
            "cf_relay_url_passes_the_engine_websocket_policy" );
        string longest = "https://" + new string( 'a', 63 ) + ".trycloudflare.com";
        assert( RelayProtocol.TryValidateUrl( CloudflaredRules.ToRelayUrl( longest ), out _, out _ ), "cf_relay_url_of_longest_label_passes_the_engine_websocket_policy" );
        assert( RelayProtocol.IsPublicRelay( url ), "cf_relay_url_is_a_public_relay" );

        assert( CloudflaredRules.ToRelayUrl( "HTTPS://Random-Words-Here.TryCloudflare.COM" ) == "wss://random-words-here.trycloudflare.com/relay", "cf_relay_url_normalises_case" );

        assert( CloudflaredRules.ToRelayUrl( "https://x.trycloudflare.com/path" ) == null, "cf_relay_url_of_address_with_path_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "https://x.trycloudflare.com/" ) == null, "cf_relay_url_of_address_with_root_path_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "https://x.trycloudflare.com:443" ) == null, "cf_relay_url_of_address_with_port_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "https://api.trycloudflare.com" ) == null, "cf_relay_url_of_api_address_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "http://x.trycloudflare.com" ) == null, "cf_relay_url_of_http_address_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "https://x.trycloudflare.com.evil.com" ) == null, "cf_relay_url_of_lookalike_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "wss://x.trycloudflare.com/relay" ) == null, "cf_relay_url_of_an_existing_relay_url_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "not an address at all" ) == null, "cf_relay_url_of_garbage_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "|  https://x.trycloudflare.com  |" ) == null, "cf_relay_url_of_a_banner_line_is_null" );
        assert( CloudflaredRules.ToRelayUrl( " https://x.trycloudflare.com" ) == null, "cf_relay_url_of_address_with_leading_space_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "https://x.trycloudflare.com " ) == null, "cf_relay_url_of_address_with_trailing_space_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "see https://x.trycloudflare.com" ) == null, "cf_relay_url_does_not_pull_an_address_out_of_prose" );
        assert( CloudflaredRules.ToRelayUrl( null ) == null, "cf_relay_url_of_null_is_null" );
        assert( CloudflaredRules.ToRelayUrl( "" ) == null, "cf_relay_url_of_empty_is_null" );
    }

    private static void Arguments( Action<bool, string> assert )
    {
        assert( CloudflaredRules.BuildArguments( 8080 ) == "tunnel --url http://localhost:8080 --no-autoupdate", "cf_arguments_exact_text_for_8080" );
        assert( CloudflaredRules.BuildArguments( 1 ) == "tunnel --url http://localhost:1 --no-autoupdate", "cf_arguments_lowest_port" );
        assert( CloudflaredRules.BuildArguments( 65535 ) == "tunnel --url http://localhost:65535 --no-autoupdate", "cf_arguments_highest_port" );

        assert( Throws<ArgumentOutOfRangeException>( () => CloudflaredRules.BuildArguments( 0 ) ), "cf_arguments_port_0_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => CloudflaredRules.BuildArguments( 65536 ) ), "cf_arguments_port_65536_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => CloudflaredRules.BuildArguments( -1 ) ), "cf_arguments_negative_port_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => CloudflaredRules.BuildArguments( int.MinValue ) ), "cf_arguments_int_min_port_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => CloudflaredRules.BuildArguments( int.MaxValue ) ), "cf_arguments_int_max_port_throws" );
    }

    private static void DownloadCheck( Action<bool, string> assert )
    {
        const string empty = "The downloaded tool was empty.";
        const string badExpected = "The expected checksum is not valid.";
        const string mismatch = "The downloaded tool does not match its expected checksum and was not run.";

        byte[] bytes = new byte[1024];
        for ( int i = 0; i < bytes.Length; i++ )
            bytes[i] = (byte)( i * 31 + 7 );
        byte[] hash = SHA256.HashData( bytes );
        string lower = Convert.ToHexString( hash ).ToLowerInvariant();
        string upper = lower.ToUpperInvariant();

        assert( lower.Length == 64 && lower != upper, "cf_download_fixture_hash_has_letters_in_both_cases" );
        assert( CloudflaredRules.GetDownloadError( bytes, lower ) == null, "cf_download_correct_lower_case_hash_accepted" );
        assert( CloudflaredRules.GetDownloadError( bytes, upper ) == null, "cf_download_correct_upper_case_hash_accepted" );

        string mixed = lower.Substring( 0, 32 ) + upper.Substring( 32 );
        assert( CloudflaredRules.GetDownloadError( bytes, mixed ) == null, "cf_download_correct_mixed_case_hash_accepted" );

        // One flipped bit anywhere must refuse: in the data, and at both ends of the expected hash (a comparison that stopped early would miss the last byte).
        byte[] flippedData = (byte[])bytes.Clone();
        flippedData[500] ^= 0x01;
        assert( CloudflaredRules.GetDownloadError( flippedData, lower ) == mismatch, "cf_download_one_flipped_bit_in_the_data_refused" );

        byte[] flippedFirst = (byte[])hash.Clone();
        flippedFirst[0] ^= 0x01;
        assert( CloudflaredRules.GetDownloadError( bytes, Convert.ToHexString( flippedFirst ) ) == mismatch, "cf_download_one_flipped_bit_in_first_hash_byte_refused" );

        byte[] flippedMiddle = (byte[])hash.Clone();
        flippedMiddle[16] ^= 0x80;
        assert( CloudflaredRules.GetDownloadError( bytes, Convert.ToHexString( flippedMiddle ) ) == mismatch, "cf_download_one_flipped_bit_in_middle_hash_byte_refused" );

        byte[] flippedLast = (byte[])hash.Clone();
        flippedLast[31] ^= 0x01;
        assert( CloudflaredRules.GetDownloadError( bytes, Convert.ToHexString( flippedLast ) ) == mismatch, "cf_download_one_flipped_bit_in_last_hash_byte_refused" );

        assert( CloudflaredRules.GetDownloadError( bytes, new string( '0', 64 ) ) == mismatch, "cf_download_all_zero_hash_refused" );

        assert( CloudflaredRules.GetDownloadError( Array.Empty<byte>(), lower ) == empty, "cf_download_empty_bytes_refused" );
        string emptyHash = Convert.ToHexString( SHA256.HashData( Array.Empty<byte>() ) );
        assert( CloudflaredRules.GetDownloadError( Array.Empty<byte>(), emptyHash ) == empty, "cf_download_empty_bytes_refused_even_with_the_hash_of_nothing" );
        assert( CloudflaredRules.GetDownloadError( null, lower ) == empty, "cf_download_null_bytes_refused" );

        // The expected hash is the code's own pinned value: a malformed one must refuse everything, never fall back to "no check".
        assert( CloudflaredRules.GetDownloadError( bytes, lower.Substring( 0, 63 ) ) == badExpected, "cf_download_63_character_expected_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, lower + "0" ) == badExpected, "cf_download_65_character_expected_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, lower.Substring( 0, 63 ) + "g" ) == badExpected, "cf_download_non_hex_character_expected_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, "0x" + lower.Substring( 0, 62 ) ) == badExpected, "cf_download_0x_prefixed_expected_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, lower.Substring( 0, 63 ) + "\n" ) == badExpected, "cf_download_expected_ending_in_newline_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, " " + lower.Substring( 0, 63 ) ) == badExpected, "cf_download_expected_starting_with_space_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, new string( '\u0660', 64 ) ) == badExpected, "cf_download_expected_of_non_ascii_digits_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, "" ) == badExpected, "cf_download_empty_expected_refused" );
        assert( CloudflaredRules.GetDownloadError( bytes, null ) == badExpected, "cf_download_null_expected_refused" );
        assert( CloudflaredRules.GetDownloadError( null, null ) == empty, "cf_download_both_null_refused" );
        assert( CloudflaredRules.GetDownloadError( Array.Empty<byte>(), "zz" ) == empty, "cf_download_empty_bytes_and_malformed_expected_refused" );
    }

    private static bool Throws<T>( Action action ) where T : Exception
    {
        try
        {
            action();
        }
        catch ( T )
        {
            return true;
        }
        return false;
    }
}
