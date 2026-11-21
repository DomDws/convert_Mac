import Foundation

/// Small personal touches that never alter how files are converted.
enum EasterEggs {
    static let birthYear = 2004
    static let birthMonth = 6
    static let birthDay = 8
    static let recipientName = "Lana"
    static let senderName = "Dom"
    static let aboutCreditLine = "Special edition for Lana — installed with care by Dom."
    static let shinyOdds = 25

    static let shinySuffixes = [
        " — no clouds were harmed.",
        " (artisanally crafted on your Mac).",
        ". Shiny! ✨",
        ". Zero servers bothered.",
        " — locally sourced & organic.",
    ]

    /// Returns the current date, or the date in `CONVERT_TEST_DATE` (`YYYY-MM-DD`) when set for testing.
    static func currentDate(
        env: [String: String] = ProcessInfo.processInfo.environment,
        calendar: Calendar = .current
    ) -> Date {
        guard let raw = env["CONVERT_TEST_DATE"] else { return Date() }
        let parts = raw.split(separator: "-")
        guard parts.count == 3,
            let year = Int(parts[0]),
            let month = Int(parts[1]),
            let day = Int(parts[2])
        else { return Date() }
        let comps = DateComponents(year: year, month: month, day: day)
        guard let date = calendar.date(from: comps) else { return Date() }
        let check = calendar.dateComponents([.year, .month, .day], from: date)
        guard check.year == year, check.month == month, check.day == day else { return Date() }
        return date
    }

    static func isBirthday(on date: Date = currentDate(), calendar: Calendar = .current) -> Bool {
        let comps = calendar.dateComponents([.month, .day], from: date)
        return comps.month == birthMonth && comps.day == birthDay
    }

    static func ordinal(_ number: Int) -> String {
        let mod100 = abs(number) % 100
        if mod100 >= 11, mod100 <= 13 { return "\(number)th" }
        switch abs(number) % 10 {
        case 1: return "\(number)st"
        case 2: return "\(number)nd"
        case 3: return "\(number)rd"
        default: return "\(number)th"
        }
    }

    static func birthdayMessage(on date: Date = currentDate(), calendar: Calendar = .current) -> String? {
        guard isBirthday(on: date, calendar: calendar) else { return nil }
        let age = calendar.component(.year, from: date) - birthYear
        if age > 0 {
            return "Happy \(ordinal(age)) Birthday, \(recipientName)!\n~ \(senderName)"
        }
        return "Happy Birthday, \(recipientName)!\n~ \(senderName)"
    }

    static func standardDoneMessage(count: Int) -> String {
        "Converted \(count) \(count == 1 ? "file" : "files")"
    }

    /// Returns the normal completion text 24/25 of the time, and a rare shiny variant 1/25 of the time.
    static func doneMessage(count: Int, roll: Int? = nil, suffixIndex: Int? = nil) -> String {
        let base = standardDoneMessage(count: count)
        let pickedRoll = roll ?? Int.random(in: 0..<shinyOdds)
        guard pickedRoll == 0, !shinySuffixes.isEmpty else { return base }
        let index = abs(suffixIndex ?? Int.random(in: 0..<shinySuffixes.count)) % shinySuffixes.count
        return base + shinySuffixes[index]
    }
}
