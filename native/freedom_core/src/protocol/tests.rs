
#[cfg(test)]
mod tests {
    use crate::protocol::header::{FixedHeader, MessageType};

    use super::*;
    use crc32fast::Hasher;

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
}