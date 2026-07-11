namespace LectureSmith.Models;

public enum Course
{
    FundamentalsOfSoftwareEngineering,
    AdvancedOOP,
    DataManagement,
    Custom
}

public static class CourseExtensions
{
    public static string DisplayName(this Course course) => course switch
    {
        Course.FundamentalsOfSoftwareEngineering => "Fundamentals of Software Engineering",
        Course.AdvancedOOP => "Advanced OOP",
        Course.DataManagement => "Data Management",
        Course.Custom => "✏️ Custom (type your own)",
        _ => course.ToString()
    };

    public static string AiContext(this Course course) => course switch
    {
        Course.FundamentalsOfSoftwareEngineering =>
            "Fundamentals of Software Engineering (FSE). Covers software processes, requirements, design, testing, DevOps, and software evolution. Textbooks include Sommerville's 'Engineering Software Products' and 'Software Engineering'.",
        Course.AdvancedOOP =>
            "Advanced Object-Oriented Programming (AOOP). Covers advanced OOP concepts, design patterns, SOLID principles, and software architecture.",
        Course.DataManagement =>
            "Data Management (DM). Covers databases, SQL, data modeling, normalization, and data management principles.",
        Course.Custom =>
            "A university course. The student will provide the course name and context.",
        _ => ""
    };
}
