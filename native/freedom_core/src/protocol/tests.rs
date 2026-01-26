
#[cfg(test)]
mod tests {
    use crate::protocol::header::{FixedHeader, MessageType, HEADER_SIZE};
    use crate::protocol::packet::NetworkPacket;
    use crate::crypto::identity::NodeIdentity;

    use super::*;
    use crc32fast::Hasher;

    /// Unit test: Header Serialization with real CRC32 checksum
    /// Verifies that the header bytes are correctly laid out and the checksum matches expected value.
    #[test]
    fn test_header_serialization_with_real_checksum() {
        // 1. Arrange: Simulated Payload
        let payload = b"123456789"; // The stardard CRC32 is 0xCBF43926 for this payload

        let mut hasher = Hasher::new();
        hasher.update(payload);
        let expected_crc = hasher.finalize();

        // 2. Act: Create Header
        let header = FixedHeader::create(
            MessageType::Handshake,
            100,
            payload,
        );

        let bytes = header.to_bytes();

        // 3. Assert: Verifies layout byte-a-byte

        // Version (1)
        assert_eq!(bytes[0], 0x01);
        // Flags (1)
        assert_eq!(bytes[1], 0x00);
        // Message Type (1)
        assert_eq!(bytes[2], 0x01);
        // Reserved (0)
        assert_eq!(bytes[3], 0x00);

        // Checksum (4) - Big Endian
        let checksum_bytes = &bytes[12..16];
        let recovered_crc = u32::from_be_bytes(checksum_bytes.try_into().unwrap());

        assert_eq!(recovered_crc, expected_crc);
        assert_eq!(recovered_crc, 0xCBF43926);
    }


    // Integration test: Handshake Packet
    // Simulate the full cycle: Create -> Serialize -> Transmit -> Deserialize -> Validate
    #[test]
    fn test_full_handshake_packet_cycle() {
        // 1. SETUP: Create identities
        let node_id = NodeIdentity::generate();
        let timestamp = 1700000000; // Fake timestamp

        // 2. CREATE: Generate Handshake Payload
        let handshake_payload = node_id.sign_handshake(timestamp);
        let payload_bytes = handshake_payload.to_bytes();

        // 3. PACK: Create the NetworkPacket (Automatically calculates CRC32)
        let packet = NetworkPacket::new(
            MessageType::Handshake,
            12345, // Request ID
            payload_bytes.to_vec()
        );

        // 4. SERIALIZE: Transform into "network" bytes
        let wire_bytes = packet.to_bytes();

        // Intermediate validation: Size
        assert_eq!(wire_bytes.len(), HEADER_SIZE + 136, "Total size should be Header(16) + Payload(136)");
        // 5. DESERIALIZE: Simulate receiving on the other side
        let received_packet = NetworkPacket::from_bytes(&wire_bytes)
            .expect("Failed to parse valid packet");

        // 6. VALIDATE: Verify integrity and data
        assert_eq!(received_packet.header.message_type, MessageType::Handshake);
        assert_eq!(received_packet.header.request_id, 12345);
        assert_eq!(received_packet.header.payload_length, 136);

        // The CRC32 should match the one calculated at sending
        assert_eq!(received_packet.header.checksum, packet.header.checksum);

        // 7. PAYLOAD PARSE: Extract and verify signature
        let received_handshake = crate::crypto::handshake::HandshakePayload::from_bytes(&received_packet.payload)
            .expect("Failed to parse handshake payload");

        // Verify cryptographic signature
        received_handshake.verify().expect("Invalid signature at destination");
        
        assert_eq!(received_handshake.timestamp, timestamp);
    }

}